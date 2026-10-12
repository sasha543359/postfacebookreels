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
            // Сколько ждём — скрипт по этому выбирает пресет x264, чтобы
            // уложиться: длинный ролик сжимается быстрее, чем короткий мем.
            psi.ArgumentList.Add("--budget");
            psi.ArgumentList.Add(_settings.TimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
                var report = new System.Text.StringBuilder();
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) err.AppendLine(e.Data); };
                process.OutputDataReceived += (_, e) => { if (e.Data != null) report.AppendLine(e.Data); };

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
                    try { process.Kill(true); process.WaitForExit(5000); } catch { }
                    // Недописанный результат, кроме нас, удалить некому: конвейер
                    // знает только исходник. Замер 18.09.2026 на дроплете за $6:
                    // 16 с ролика 1080p60 обрабатывались 252 с, и обрывки по 10 МБ
                    // оставались в /var/www/videos после каждого таймаута.
                    TryDelete(outputPath);
                    _logger.LogWarning($"   ⚠️ Уникализация превысила таймаут {_settings.TimeoutSeconds}с — публикуем исходник.");
                    return inputPath;
                }

                // Код 3 — скрипт сам прикинул, что не уложится в таймаут даже на
                // самом быстром пресете, и не стал начинать (pick_preset в
                // uniquify.py). Это не сбой: ролик уйдёт без уникализации, а
                // EnsureReelsResolutionAsync растянет его до 1080x1920.
                if (process.ExitCode == 3)
                {
                    TryDelete(outputPath);
                    _logger.LogWarning($"   ⏱️ Ролик слишком длинный, чтобы уникализировать его за {_settings.TimeoutSeconds}с — публикуем без уникализации.");
                    return inputPath;
                }

                if (process.ExitCode != 0 || !File.Exists(outputPath))
                {
                    TryDelete(outputPath);
                    _logger.LogWarning($"   ⚠️ Уникализатор вернул код {process.ExitCode}. Публикуем исходник.\n{TrimErr(err.ToString())}");
                    return inputPath;
                }

                // Какой пресет скрипт выбрал под таймаут — пригодится, когда
                // будут спрашивать, почему один ролик сжат иначе, чем другой.
                var encode = System.Text.RegularExpressions.Regex.Match(
                    report.ToString(), "\"crf\":\\s*(\\d+),\\s*\"preset\":\\s*\"(\\w+)\"");
                _logger.LogInformation($"   ✅ Уникализировано: {Path.GetFileName(outputPath)}" +
                                       (encode.Success ? $" (x264 {encode.Groups[2].Value}, crf {encode.Groups[1].Value})" : string.Empty));
                return outputPath;
            }
            catch (Exception ex)
            {
                TryDelete(outputPath);
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

        // ─────────────────────────────────────────────────────────────────────
        //  Разрешение под Reels
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Пресет x264 для растяжки. Растяжка срабатывает как раз тогда, когда
        /// на тяжёлую работу времени не осталось: уникализатор выключен, упал
        /// или не уложился в таймаут — а это длинные ролики на сервере с одним
        /// ядром. Поэтому быстрый пресет, а качество держит crf.
        /// </summary>
        private const string UpscalePreset = "veryfast";

        /// <summary>
        /// Потолок на растяжку. Не уложилась — публикуем как есть: ролик в
        /// 576x1024 лучше, чем никакого.
        /// </summary>
        private const int UpscaleTimeoutSeconds = 600;

        public async Task<string> EnsureReelsResolutionAsync(string path)
        {
            try
            {
                if (!File.Exists(path)) return path;

                var info = await MediaProbe.ProbeAsync(path);

                // Не прочиталось — растягивать вслепую нельзя, пусть решает Facebook.
                if (info == null || info.ShortSide >= HdWaitPolicy.FullHdSide) return path;

                var dir = Path.GetDirectoryName(path) ?? ".";
                var upscaled = Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + "_1080.mp4");

                _logger.LogInformation(
                    $"   🔍 Файл {info.Width}×{info.Height} — меньше 1080×1920: Facebook пометит такой ролик " +
                    "как «нет высокого разрешения». Растягиваем до 1080×1920...");

                var stopwatch = Stopwatch.StartNew();

                // Кадрирование то же, что у уникализатора: вертикальный ролик
                // просто растягивается, у ролика с другими пропорциями срезаются
                // края — чёрные поля Facebook тоже считает недостатком.
                // lanczos вместо bicubic по умолчанию: при растяжке почти вдвое
                // картинка у него чётче (+1…1,4 VMAF, замер 12.10.2026).
                // Больше 30 к/с не нужно — уникализатор тоже отдаёт 30, а
                // 60-кадровый ролик растягивался бы почти вдвое дольше.
                var filters = (info.Fps > 30.5 ? "fps=30," : string.Empty) +
                              "scale=1080:1920:force_original_aspect_ratio=increase:flags=lanczos,crop=1080:1920,setsar=1";

                var ok = await RunAsync("ffmpeg", new[]
                {
                    "-y", "-i", path,
                    "-vf", filters,
                    "-c:v", "libx264", "-preset", UpscalePreset, "-crf", "20",
                    "-profile:v", "high", "-pix_fmt", "yuv420p",
                    "-c:a", "aac", "-b:a", "128k", "-ar", "48000", "-ac", "2",
                    "-map_metadata", "-1", "-movflags", "+faststart",
                    upscaled
                }, TimeSpan.FromSeconds(UpscaleTimeoutSeconds));

                if (!ok || !File.Exists(upscaled) || new FileInfo(upscaled).Length < 10 * 1024)
                {
                    TryDelete(upscaled);
                    _logger.LogWarning($"   ⚠️  Растянуть не вышло — публикуем {info.Width}×{info.Height} как есть");
                    return path;
                }

                TryDelete(path);
                _logger.LogInformation($"   ✅ Растянуто до 1080×1920 за {stopwatch.Elapsed.TotalSeconds:F0} с");
                return upscaled;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"   ⚠️  Проверка разрешения не удалась ({ex.Message}) — отдаём как есть");
                return path;
            }
        }

        /// <param name="timeout">null — ждать сколько угодно. Не уложился — процесс снимается, ответ false.</param>
        private static async Task<bool> RunAsync(string file, string[] arguments, TimeSpan? timeout = null)
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

            using var cts = timeout.HasValue ? new CancellationTokenSource(timeout.Value) : new CancellationTokenSource();
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); process.WaitForExit(5000); } catch { }
                return false;
            }

            await Task.WhenAll(stdout, stderr);

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
