using System.Diagnostics;
using FacebookReelsPublisher.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FacebookReelsPublisher.Services
{
    /// <summary>
    /// Прогоняет скачанный TikTok-клип через uniquify.py (ffmpeg): снятие
    /// watermark, ре-энкод, своя сигнатура под каждую Страницу, звук,
    /// опциональная экранная подпись. Возвращает путь к новому файлу в той же
    /// папке (её раздаёт nginx), чтобы Facebook забрал уже уникализированную
    /// версию.
    /// </summary>
    public class VideoUniquifierService : IVideoUniquifierService
    {
        private readonly UniquifierSettings _settings;
        private readonly ILogger<VideoUniquifierService> _logger;

        public VideoUniquifierService(IOptions<UniquifierSettings> settings, ILogger<VideoUniquifierService> logger)
        {
            _settings = settings.Value;
            _logger = logger;
        }

        public bool Enabled => _settings.Enabled;

        public async Task<string> UniquifyAsync(string inputPath, string profile, string? onScreenCaption = null)
        {
            if (!_settings.Enabled)
                return inputPath;

            if (!File.Exists(inputPath))
                throw new FileNotFoundException("Исходный файл для уникализации не найден", inputPath);

            var dir = Path.GetDirectoryName(inputPath) ?? ".";
            var stem = Path.GetFileNameWithoutExtension(inputPath);
            var safeProfile = new string((profile ?? "acc").Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray());
            if (string.IsNullOrEmpty(safeProfile)) safeProfile = "acc";
            var outputPath = Path.Combine(dir, $"{stem}_{safeProfile}.mp4");

            var psi = new ProcessStartInfo
            {
                FileName = _settings.PythonPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            // ArgumentList корректно экранирует пробелы/кириллицу в подписи и путях.
            psi.ArgumentList.Add(_settings.ScriptPath);
            psi.ArgumentList.Add(inputPath);
            psi.ArgumentList.Add(outputPath);
            psi.ArgumentList.Add("--profile"); psi.ArgumentList.Add(profile ?? "default");
            psi.ArgumentList.Add("--mode"); psi.ArgumentList.Add(_settings.Mode);
            psi.ArgumentList.Add("--audio"); psi.ArgumentList.Add(_settings.Audio);
            if (!string.IsNullOrWhiteSpace(_settings.Font))
            {
                psi.ArgumentList.Add("--font"); psi.ArgumentList.Add(_settings.Font);
            }
            if (!string.IsNullOrWhiteSpace(onScreenCaption))
            {
                psi.ArgumentList.Add("--caption"); psi.ArgumentList.Add(onScreenCaption);
            }
            // uniquify.py печатает UTF-8; заставим python не падать на кодировке в контейнере.
            psi.Environment["PYTHONUTF8"] = "1";
            psi.Environment["PYTHONIOENCODING"] = "utf-8";

            _logger.LogInformation($"   🎛️  Уникализация (профиль={profile}, mode={_settings.Mode}, audio={_settings.Audio})...");

            try
            {
                using var process = new Process { StartInfo = psi };
                var err = new System.Text.StringBuilder();
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) err.AppendLine(e.Data); };
                process.OutputDataReceived += (_, e) => { /* JSON-отчёт скрипта нам не нужен построчно */ };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.TimeoutSeconds));
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(true); } catch { }
                    _logger.LogWarning($"   ⚠️ Уникализация превысила таймаут {_settings.TimeoutSeconds}с — публикуем исходник.");
                    return inputPath;
                }

                if (process.ExitCode != 0 || !File.Exists(outputPath))
                {
                    _logger.LogWarning($"   ⚠️ Уникализатор вернул код {process.ExitCode}. Публикуем исходник.\n{TrimErr(err.ToString())}");
                    return inputPath;
                }

                _logger.LogInformation($"   ✅ Уникализировано: {Path.GetFileName(outputPath)}");
                return outputPath;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "   ⚠️ Ошибка уникализации — публикуем исходник.");
                return inputPath;
            }
        }

        private static string TrimErr(string s) => s.Length > 1500 ? s[^1500..] : s;

        // ─────────────────────────────────────────────────────────────────────
        //  Длительность под Reels
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Потолок Facebook Reels — 90 секунд.</summary>
        private const double MaxReelSeconds = 90.0;

        /// <summary>
        /// До скольких секунд режем на самом деле.
        ///
        /// Не 90, а 89,5 — из-за того, как работает резка без пережатия. «-c copy»
        /// рвёт поток по границам пакетов, а не по точной отметке, и попадает
        /// чуть ЗА неё: замер на реальном ролике дал 90,067 секунды при запросе
        /// ровно 90. Для Facebook это уже за пределом, и ролик вернулся бы с
        /// ошибкой 1363128 — то есть защита от слишком длинных роликов сама
        /// делала бы их слишком длинными. Полсекунды запаса снимают вопрос.
        /// </summary>
        private const double TrimTargetSeconds = 89.5;

        public async Task<string> EnsureReelsDurationAsync(string path)
        {
            try
            {
                if (!File.Exists(path)) return path;

                var duration = await ProbeDurationAsync(path);

                // 0 означает «длительность не прочиталась». Резать вслепую нельзя:
                // испортим нормальный ролик. Пусть решает Facebook.
                if (duration <= 0 || duration <= MaxReelSeconds) return path;

                var dir = Path.GetDirectoryName(path) ?? ".";
                var trimmed = Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + "_90s.mp4");

                _logger.LogInformation(
                    $"   ✂️  Ролик длиннее 90 секунд ({duration:F1}с) — подрезаем, иначе Facebook его не примет");

                // «-c copy» — без пережатия: режем по границам пакетов. Для конца
                // ролика этого достаточно, а пережимать 90 секунд ради обрезки
                // было бы расточительно.
                var ok = await RunAsync("ffmpeg", new[]
                {
                    "-y", "-i", path,
                    "-t", TrimTargetSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    "-c", "copy", "-movflags", "+faststart",
                    trimmed
                });

                if (!ok || !File.Exists(trimmed) || new FileInfo(trimmed).Length < 10 * 1024)
                {
                    _logger.LogWarning("   ⚠️  Подрезать не вышло — отдаём как есть");
                    TryDelete(trimmed);
                    return path;
                }

                TryDelete(path);
                return trimmed;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"   ⚠️  Проверка длительности не удалась ({ex.Message}) — отдаём как есть");
                return path;
            }
        }

        private async Task<double> ProbeDurationAsync(string path)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffprobe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-show_entries");
            psi.ArgumentList.Add("format=duration");
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
            psi.ArgumentList.Add(path);

            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            return double.TryParse(
                stdout.Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var seconds)
                ? seconds
                : 0;
        }

        private static async Task<bool> RunAsync(string file, string[] arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var a in arguments) psi.ArgumentList.Add(a);

            using var process = new Process { StartInfo = psi };
            process.Start();

            // Читаем оба потока: ffmpeg пишет много, и полный буфер трубы
            // остановил бы процесс навсегда.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            await Task.WhenAll(stdout, stderr);
            await process.WaitForExitAsync();

            return process.ExitCode == 0;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
