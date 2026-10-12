using System.Diagnostics;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace FacebookReelsPublisher.Services
{
    /// <summary>
    /// Что внутри видео: размер кадра, частота кадров, кодек, длительность.
    /// </summary>
    public sealed record MediaInfo(int Width, int Height, double Fps, string Codec, double Duration)
    {
        /// <summary>
        /// Короткая сторона кадра. «1080p» — это про неё: у вертикального
        /// ролика 1080x1920 короткая сторона — ширина.
        /// </summary>
        public int ShortSide => Math.Min(Width, Height);

        /// <summary>Для лога: «576×1024, 30 к/с, h264».</summary>
        public string Describe()
        {
            var parts = new List<string> { $"{Width}×{Height}" };
            if (Fps > 0) parts.Add($"{Fps.ToString("0.##", CultureInfo.InvariantCulture)} к/с");
            if (!string.IsNullOrWhiteSpace(Codec) && Codec != "NA") parts.Add(Codec);
            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// ffprobe над готовым файлом. Нужен по двум причинам. Лог: без него не
    /// видно, в каком качестве ролик пришёл из TikTok и в каком ушёл в
    /// Facebook, — а именно этого не хватило, чтобы сразу понять жалобу
    /// «Facebook пишет, что разрешение низкое». И страховка: файл меньше
    /// 1080x1920 перед публикацией надо распознать, чтобы его растянуть.
    /// </summary>
    public static class MediaProbe
    {
        /// <summary>Сведения о первой видеодорожке; null — файл не читается.</summary>
        public static async Task<MediaInfo?> ProbeAsync(string path)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "ffprobe",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var a in new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path })
                {
                    psi.ArgumentList.Add(a);
                }

                using var process = new Process { StartInfo = psi };
                process.Start();

                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                await Task.WhenAll(stdout, stderr);
                await process.WaitForExitAsync();

                if (process.ExitCode != 0) return null;

                var json = JObject.Parse(stdout.Result);
                var video = (json["streams"] as JArray)?
                    .OfType<JObject>()
                    .FirstOrDefault(s => (string?)s["codec_type"] == "video");
                if (video == null) return null;

                var width = (int?)video["width"] ?? 0;
                var height = (int?)video["height"] ?? 0;
                if (width <= 0 || height <= 0) return null;

                // avg_frame_rate честнее r_frame_rate, но у части файлов он «0/0».
                var fps = ParseRate((string?)video["avg_frame_rate"]);
                if (fps <= 0) fps = ParseRate((string?)video["r_frame_rate"]);

                double.TryParse((string?)json["format"]?["duration"], NumberStyles.Float, CultureInfo.InvariantCulture, out var duration);

                return new MediaInfo(width, height, fps, (string?)video["codec_name"] ?? string.Empty, duration);
            }
            catch
            {
                // ffprobe нет или ответ не разобрался — лог и страховка
                // обойдутся без этих сведений, публикация от этого не зависит.
                return null;
            }
        }

        private static double ParseRate(string? rate)
        {
            if (string.IsNullOrWhiteSpace(rate)) return 0;

            var parts = rate.Split('/');
            if (parts.Length == 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
                && den > 0)
            {
                return num / den;
            }

            return double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
        }
    }
}
