using System.Net.Http.Headers;
using System.Text;
using FacebookReelsPublisher.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace FacebookReelsPublisher.Services
{
    /// <summary>
    /// Публикация Reels на Страницу Facebook.
    ///
    /// ЧЕМ ЭТО ОТЛИЧАЕТСЯ ОТ INSTAGRAM-ВЕРСИИ
    /// ────────────────────────────────────────
    /// В Instagram публикация — два запроса: «создай контейнер по ссылке на mp4»
    /// и «опубликуй контейнер». У Facebook путь длиннее и живёт на ДВУХ разных
    /// хостах:
    ///
    ///   1. start   POST graph.facebook.com/{page-id}/video_reels   → video_id + upload_url
    ///   2. upload  POST rupload.facebook.com/video-upload/{video_id}
    ///   3. finish  POST graph.facebook.com/{page-id}/video_reels   → опубликовано
    ///
    /// Второй шаг — единственный, который ходит на rupload.facebook.com. Если
    /// отправить его на graph.facebook.com, ответ будет про «неизвестный путь»,
    /// и это самая частая ошибка при переносе с Instagram.
    ///
    /// Файл можно отдать двумя способами, и оба здесь есть:
    ///   • ссылкой  — заголовок «file_url», Facebook скачивает сам (как Instagram);
    ///   • байтами  — тело запроса, публичный адрес не нужен вообще.
    /// Режим задаётся в appsettings.json → Server.UploadMode.
    /// </summary>
    public class FacebookReelsService : IFacebookReelsService
    {
        private readonly FacebookSettings _settings;
        private readonly ILogger<FacebookReelsService> _logger;
        private readonly HttpClient _httpClient;

        private string GraphBase => $"https://graph.facebook.com/{_settings.ApiVersion}";
        private string UploadBase => $"https://rupload.facebook.com/video-upload/{_settings.ApiVersion}";

        /// <summary>Пауза между опросами статуса обработки.</summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

        public FacebookReelsService(
            IOptions<FacebookSettings> settings,
            ILogger<FacebookReelsService> logger,
            HttpClient httpClient)
        {
            _settings = settings.Value;
            _logger = logger;
            _httpClient = httpClient;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Публикация целиком
        // ─────────────────────────────────────────────────────────────────────

        public async Task<PublishResult> PublishReelAsync(VideoPublishInfo videoInfo)
        {
            _lastErrorCode = 0;

            try
            {
                // ── Шаг 1. Инициализируем сеанс загрузки ─────────────────────
                _logger.LogInformation("   1/3 Начинаем сеанс загрузки Reels...");

                var videoId = await StartUploadSessionAsync();
                if (string.IsNullOrEmpty(videoId))
                {
                    return Fail("Facebook не выдал video_id (шаг start не прошёл)");
                }

                _logger.LogInformation($"   ✓ Сеанс начат, video_id = {videoId}");

                // ── Шаг 2. Отдаём файл ───────────────────────────────────────
                _logger.LogInformation("   2/3 Передаём файл в Facebook...");

                var transferError = await TransferAsync(videoId, videoInfo);
                if (transferError != null)
                {
                    return Fail($"не удалось передать файл: {transferError}", videoId);
                }

                // До finish ждём только ЗАГРУЗКУ. Обработку Facebook запускает
                // лишь после finish (проверено вживую: до него processing_phase
                // стоит в "not_started" сколько угодно). Байтами файл приходит
                // сразу, а по ссылке Facebook скачивает его сам и не мгновенно.
                var uploaded = await WaitForUploadAsync(videoId);

                if (uploaded.IsError)
                {
                    return FailWithStatus("Facebook не принял файл", uploaded, videoId);
                }

                if (!uploaded.IsUploaded)
                {
                    // Файл так и не доехал, но и ошибки нет. Пробуем finish:
                    // если файла у Facebook нет, он откажет внятно, а ролик не
                    // отметится в истории и уйдёт в следующий цикл.
                    _logger.LogWarning(
                        $"   ⚠️  Загрузка не подтвердилась за {_settings.ProcessingTimeoutSeconds}с " +
                        $"(загрузка: {Or(uploaded.UploadingPhase)}). Пробуем опубликовать как есть.");
                }
                else
                {
                    _logger.LogInformation("   ✓ Файл у Facebook");
                }

                // ── Обложка (необязательно) ──────────────────────────────────
                // Ставим ДО публикации, чтобы Reels сразу вышел с нужной картинкой.
                // Ошибка здесь публикацию не отменяет: без обложки Facebook
                // возьмёт кадр из видео, и это лучше, чем не выложить ролик.
                if (!string.IsNullOrWhiteSpace(videoInfo.CoverPath))
                {
                    await TrySetThumbnailAsync(videoId, videoInfo.CoverPath!);
                }

                // ── Шаг 3. Публикуем ─────────────────────────────────────────
                _logger.LogInformation("   3/3 Публикуем Reels...");

                var (published, postId, publishError) = await FinishAsync(videoId, videoInfo.Description);

                if (!published)
                {
                    return Fail($"публикация отклонена: {publishError}", videoId);
                }

                // finish только ставит ролик в очередь: обработка и выход в ленту
                // идут после него. Отказ по самому файлу (разрешение, длительность)
                // приходит именно здесь, поэтому успех засчитываем, лишь увидев
                // исход, — иначе негодный ролик считался бы опубликованным.
                _logger.LogInformation("   ⏳ Ждём, пока Facebook обработает и выпустит Reels...");

                var final = await WaitForPublishAsync(videoId);

                // Проваливаем только отказ, который сообщил сам Facebook. Такой
                // отказ окончательный (afterFinish): тот же файл получит тот же
                // ответ, а повторять — значит заливать его в каждом цикле.
                if (final.IsError)
                {
                    // Окончательный — только отказ по самому файлу, который
                    // целиком дошёл до Facebook. upload_failed/expired — сбой
                    // доставки, а не файла: такой ролик уходит в следующий цикл.
                    var fileRejected = final.IsUploaded
                        && final.VideoStatus is not ("upload_failed" or "expired");

                    return FailWithStatus("Facebook забраковал видео после публикации", final, videoId, afterFinish: fileRejected);
                }

                if (final.IsPublished || final.IsReady)
                {
                    _logger.LogInformation("   ✅ Reels опубликован");
                }
                else
                {
                    // finish принят, отказа нет — Facebook не успел или статус не
                    // читался. Считаем опубликованным: повторная заливка дала бы дубль.
                    _logger.LogWarning(
                        $"   ⚠️  Facebook принял Reels, но выход ещё не подтвердился " +
                        $"(обработка: {Or(final.ProcessingPhase)}, публикация: {Or(final.PublishingPhase)}). " +
                        "Ролик выйдет сам.");
                }

                return new PublishResult
                {
                    Success = true,
                    VideoId = videoId,
                    PostId = postId
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при публикации Reels");
                return Fail(ex.Message);
            }
        }

        /// <summary>
        /// Код последней ошибки Graph API. Заполняется при разборе ответа, чтобы
        /// вызывающий код мог отличить «файл не годится» от «попробуй ещё раз».
        /// Сервис создаётся отдельно под каждую Страницу и работает
        /// последовательно, так что поля здесь достаточно.
        /// </summary>
        private int _lastErrorCode;

        private PublishResult Fail(string message, string? videoId = null) =>
            new PublishResult
            {
                Success = false,
                ErrorMessage = message,
                VideoId = videoId,
                ErrorCode = _lastErrorCode
            };

        /// <summary>
        /// Отказ, пришедший в статусе видео, а не ответом на запрос. Код берём
        /// оттуда же: без него «файл не годится никогда» не отличить от
        /// временного сбоя, и негодный ролик заливался бы в каждом цикле.
        /// </summary>
        private PublishResult FailWithStatus(string what, ReelStatus status, string videoId, bool afterFinish = false)
        {
            // Без условия: код от прошлого, некритичного шага (например, отказ
            // обложки) не должен приклеиться к этому отказу.
            _lastErrorCode = status.ErrorCode;

            var hint = Hint(status.ErrorCode, 0);
            var details = status.ErrorMessage ?? $"статус {status.VideoStatus}";
            if (status.ErrorCode != 0) details = $"код {status.ErrorCode} | {details}";
            if (hint != null) details += $" | → {hint}";

            var result = Fail($"{what}: {details}", videoId);
            result.RejectedAfterFinish = afterFinish;
            return result;
        }

        private static string Or(string s) => string.IsNullOrEmpty(s) ? "—" : s;

        // ─────────────────────────────────────────────────────────────────────
        //  Шаг 1: start
        // ─────────────────────────────────────────────────────────────────────

        private async Task<string?> StartUploadSessionAsync()
        {
            var url = $"{GraphBase}/{_settings.PageId}/video_reels";

            // Документация показывает именно JSON-тело. Форма (form-urlencoded)
            // тоже проходит, но JSON — то, что описано, и меньше поводов для
            // сюрпризов при смене версии API.
            var body = new JObject
            {
                ["upload_phase"] = "start",
                ["access_token"] = _settings.PageAccessToken
            }.ToString();

            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(url, content);
            var text = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"   ❌ start не прошёл: {DescribeError(text, response)}");
                return null;
            }

            return JObject.Parse(text)["video_id"]?.ToString();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Шаг 2: передача файла
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Отдаёт файл Facebook. Возвращает null при успехе или текст ошибки.</summary>
        private async Task<string?> TransferAsync(string videoId, VideoPublishInfo info)
        {
            var mode = (_settings.UploadMode ?? "bytes").Trim().ToLowerInvariant();
            var hasUrl = !string.IsNullOrWhiteSpace(info.VideoUrl);
            var hasFile = !string.IsNullOrWhiteSpace(info.FilePath) && File.Exists(info.FilePath);

            if (mode == "url" && !hasUrl)
            {
                return "режим \"url\", но публичная ссылка на видео не задана (проверь Server.PublicUrl)";
            }

            if (mode == "bytes" && !hasFile)
            {
                return "режим \"bytes\", но локального файла нет";
            }

            // Ссылкой — если попросили или если это «auto» и ссылка есть.
            if (hasUrl && mode != "bytes")
            {
                _logger.LogInformation($"   ↗ отдаём ссылкой: {info.VideoUrl}");

                var urlError = await TransferByUrlAsync(videoId, info.VideoUrl!);
                if (urlError == null)
                {
                    return null;
                }

                if (mode == "url" || !hasFile)
                {
                    return urlError;
                }

                // «auto»: Facebook не сумел забрать файл сам. Причины обычно
                // внешние — сервер не виден снаружи, порт закрыт, адрес указан
                // неверно. Байты через это всё проходят, поэтому пробуем их.
                _logger.LogWarning($"   ⚠️  по ссылке не вышло ({urlError})");
                _logger.LogWarning("   ➡️  заливаем файл напрямую, без ссылки");
            }

            if (!hasFile)
            {
                return "нет ни рабочей ссылки, ни локального файла";
            }

            return await TransferByBytesAsync(videoId, info.FilePath);
        }

        /// <summary>
        /// Facebook скачивает файл сам по ссылке. Требование Meta: сайт должен
        /// пускать робота «facebookexternalhit/1.1» и не закрывать файл через
        /// robots.txt — иначе запрос отобьётся, даже если ссылка открывается
        /// в браузере.
        /// </summary>
        private async Task<string?> TransferByUrlAsync(string videoId, string fileUrl)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{UploadBase}/{videoId}");
            request.Headers.TryAddWithoutValidation("Authorization", $"OAuth {_settings.PageAccessToken}");
            request.Headers.TryAddWithoutValidation("file_url", fileUrl);

            try
            {
                using var response = await _httpClient.SendAsync(request);
                var text = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return DescribeError(text, response);
                }

                if ((bool?)JObject.Parse(text)["success"] == true)
                {
                    return null;
                }

                return $"Facebook ответил без success: {Trim(text)}";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// Заливаем файл телом запроса. Публичный адрес при этом не нужен вовсе —
        /// этим режимом программа работает и на машине без белого IP.
        ///
        /// Оборванную заливку Facebook разрешает продолжить с места обрыва:
        /// в статусе лежит bytes_transferred, его и ставим в offset. Поэтому при
        /// сбое мы не начинаем сеанс заново, а дозаливаем хвост.
        /// </summary>
        private async Task<string?> TransferByBytesAsync(string videoId, string path)
        {
            var size = new FileInfo(path).Length;
            _logger.LogInformation($"   ↗ заливаем файл: {Path.GetFileName(path)} ({size / 1024.0 / 1024.0:F2} МБ)");

            long offset = 0;
            string? lastError = null;

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (offset > 0)
                    {
                        stream.Seek(offset, SeekOrigin.Begin);
                        _logger.LogInformation($"   ↻ продолжаем с байта {offset} из {size}");
                    }

                    using var request = new HttpRequestMessage(HttpMethod.Post, $"{UploadBase}/{videoId}");
                    request.Headers.TryAddWithoutValidation("Authorization", $"OAuth {_settings.PageAccessToken}");
                    request.Headers.TryAddWithoutValidation("offset", offset.ToString());
                    request.Headers.TryAddWithoutValidation("file_size", size.ToString());

                    var content = new StreamContent(stream);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    request.Content = content;

                    using var response = await _httpClient.SendAsync(request);
                    var text = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode && (bool?)JObject.Parse(text)["success"] == true)
                    {
                        return null;
                    }

                    lastError = response.IsSuccessStatusCode
                        ? $"Facebook ответил без success: {Trim(text)}"
                        : DescribeError(text, response);
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }

                if (attempt == 3) break;

                _logger.LogWarning($"   ⚠️  заливка, попытка {attempt} из 3: {lastError}");

                // Сколько байт Facebook успел принять — спрашиваем у него же.
                // Без этого повтор начинался бы с нуля и на плохой связи
                // не закончился бы никогда.
                var status = await CheckStatusAsync(videoId);
                offset = status.BytesTransferred > 0 && status.BytesTransferred < size
                    ? status.BytesTransferred
                    : 0;

                await Task.Delay(TimeSpan.FromSeconds(5));
            }

            return lastError;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Статус
        // ─────────────────────────────────────────────────────────────────────

        public async Task<ReelStatus> CheckStatusAsync(string videoId)
        {
            var result = new ReelStatus();

            // Сорвавшийся опрос не должен подменять код настоящей ошибки
            // публикации: DescribeError запоминает код попутно.
            var savedErrorCode = _lastErrorCode;

            try
            {
                var url = $"{GraphBase}/{videoId}?fields=status&access_token={Uri.EscapeDataString(_settings.PageAccessToken)}";
                using var response = await _httpClient.GetAsync(url);
                var text = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    result.RequestError = DescribeError(text, response);
                    _lastErrorCode = savedErrorCode;
                    return result;
                }

                var status = JObject.Parse(text)["status"] as JObject;
                if (status == null) return result;

                result.VideoStatus = status["video_status"]?.ToString() ?? string.Empty;

                var uploading = status["uploading_phase"] as JObject;
                var processing = status["processing_phase"] as JObject;
                var publishing = status["publishing_phase"] as JObject;

                result.UploadingPhase = uploading?["status"]?.ToString() ?? string.Empty;
                result.ProcessingPhase = processing?["status"]?.ToString() ?? string.Empty;
                result.PublishingPhase = publishing?["status"]?.ToString() ?? string.Empty;
                // Живой ответ пишет "bytes_transferred", а в документации Meta
                // встречается "bytes_transfered" с опечаткой. Читаем оба: с
                // одним только ошибочным докачка всегда начиналась бы с нуля.
                result.BytesTransferred =
                    (long?)uploading?["bytes_transferred"]
                    ?? (long?)uploading?["bytes_transfered"]
                    ?? 0;

                // Ошибка может лежать в любой из трёх фаз — берём первую найденную.
                var error =
                    uploading?["error"] as JObject
                    ?? processing?["error"] as JObject
                    ?? publishing?["error"] as JObject;

                result.ErrorMessage = error?["message"]?.ToString();

                if (error != null)
                {
                    result.ErrorCode = int.TryParse(error["code"]?.ToString(), out var code) && code != 0
                        ? code
                        : CodeFromMessage(result.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                result.RequestError = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Документация Meta показывает ошибку в статусе без кода — только текст
        /// («Resolution too low…»). Восстанавливаем код по тексту: без него
        /// «файл не годится никогда» не отличить от временного сбоя.
        /// </summary>
        private static int CodeFromMessage(string? message)
        {
            var m = (message ?? string.Empty).ToLowerInvariant();

            // Узкие фразы, а не отдельные слова: «разрешение» по-русски — это и
            // «права доступа», а «resolution» встречается в «name resolution».
            // Ошибочно окончательный отказ стоил бы потерянного ролика.
            if (m.Contains("aspect ratio") || m.Contains("соотношение сторон")) return 1363040;
            if (m.Contains("resolution too low") || m.Contains("minimum resolution")
                || m.Contains("низкое разрешение") || m.Contains("разрешение видео")) return 1363127;
            if (m.Contains("video duration") || m.Contains("duration of") || m.Contains("длительность видео")) return 1363128;
            if (m.Contains("frame rate") || m.Contains("частота кадров")) return 1363129;

            return 0;
        }

        /// <summary>До finish: ждём, пока файл целиком окажется у Facebook.</summary>
        private Task<ReelStatus> WaitForUploadAsync(string videoId) =>
            PollStatusAsync(videoId, s => s.IsUploaded);

        /// <summary>После finish: ждём, пока Facebook обработает и выпустит ролик.</summary>
        private Task<ReelStatus> WaitForPublishAsync(string videoId) =>
            PollStatusAsync(videoId, s => s.IsPublished || s.IsReady);

        /// <summary>
        /// Опрашивает статус, пока не выполнится условие, Facebook не сообщит
        /// об ошибке или не кончится ProcessingTimeoutSeconds. Возвращает
        /// последний статус. Сорвавшийся запрос — не ошибка видео: опрос
        /// продолжается, а в лог идёт предупреждение.
        /// </summary>
        private async Task<ReelStatus> PollStatusAsync(string videoId, Func<ReelStatus, bool> done)
        {
            var deadline = DateTime.UtcNow.AddSeconds(_settings.ProcessingTimeoutSeconds);
            var last = new ReelStatus();
            var tick = 0;
            var failures = 0;

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(PollInterval);
                tick++;

                last = await CheckStatusAsync(videoId);

                if (last.RequestFailed)
                {
                    failures++;
                    if (failures == 1 || failures % 6 == 0)
                    {
                        _logger.LogWarning($"      ⚠️  статус не прочитался ({last.RequestError}) — спрашиваем ещё раз");
                    }
                    continue;
                }

                if (last.IsError || done(last)) return last;

                // Раз в 30 секунд — строчка в лог, чтобы было видно, что не зависли.
                if (tick % 6 == 0)
                {
                    _logger.LogInformation(
                        $"      …загрузка: {Or(last.UploadingPhase)}, обработка: {Or(last.ProcessingPhase)}, " +
                        $"публикация: {Or(last.PublishingPhase)}");
                }
            }

            return last;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Обложка
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Обложка у Facebook ставится не ссылкой, как в Instagram, а файлом:
        /// POST /{video_id}/thumbnails, поле source, multipart. Для этого нужны
        /// дополнительные разрешения (pages_read_user_content,
        /// pages_manage_engagement) — если их не выдали, шаг молча отвалится,
        /// и это нормально: ролик всё равно выйдет, просто с кадром из видео.
        /// </summary>
        private async Task TrySetThumbnailAsync(string videoId, string coverPath)
        {
            try
            {
                if (!File.Exists(coverPath))
                {
                    _logger.LogWarning($"   ⚠️  Обложка не найдена: {coverPath} — публикуем без неё");
                    return;
                }

                // Ограничение Meta — 10 МБ на файл обложки.
                var size = new FileInfo(coverPath).Length;
                if (size > 10 * 1024 * 1024)
                {
                    _logger.LogWarning($"   ⚠️  Обложка тяжелее 10 МБ ({size / 1024 / 1024} МБ) — публикуем без неё");
                    return;
                }

                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(_settings.PageAccessToken), "access_token");
                form.Add(new StringContent("true"), "is_preferred");

                var bytes = await File.ReadAllBytesAsync(coverPath);
                var file = new ByteArrayContent(bytes);
                file.Headers.ContentType = new MediaTypeHeaderValue(GuessImageType(coverPath));
                form.Add(file, "source", Path.GetFileName(coverPath));

                using var response = await _httpClient.PostAsync($"{GraphBase}/{videoId}/thumbnails", form);
                var text = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation($"   🖼️  Обложка поставлена: {Path.GetFileName(coverPath)}");
                }
                else
                {
                    _logger.LogWarning($"   ⚠️  Обложку поставить не вышло ({DescribeError(text, response)}) — публикуем без неё");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"   ⚠️  Обложку поставить не вышло ({ex.Message}) — публикуем без неё");
            }
        }

        private static string GuessImageType(string path) =>
            Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".gif" => "image/gif",
                _ => "image/jpeg"
            };

        // ─────────────────────────────────────────────────────────────────────
        //  Шаг 3: finish
        // ─────────────────────────────────────────────────────────────────────

        private async Task<(bool Ok, string? PostId, string? Error)> FinishAsync(string videoId, string description)
        {
            // Параметры идут в строке запроса — так они показаны в документации,
            // и так же (через URL, а не тело) работала Instagram-версия.
            var url = $"{GraphBase}/{_settings.PageId}/video_reels" +
                      $"?access_token={Uri.EscapeDataString(_settings.PageAccessToken)}" +
                      $"&video_id={Uri.EscapeDataString(videoId)}" +
                      $"&upload_phase=finish" +
                      $"&video_state=PUBLISHED";

            if (!string.IsNullOrWhiteSpace(description))
            {
                url += $"&description={Uri.EscapeDataString(description)}";
            }

            using var response = await _httpClient.PostAsync(url, null);
            var text = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return (false, null, DescribeError(text, response));
            }

            var json = JObject.Parse(text);

            if ((bool?)json["success"] != true)
            {
                return (false, null, $"Facebook ответил без success: {Trim(text)}");
            }

            return (true, json["post_id"]?.ToString(), null);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Проверка доступа (режим --check)
        // ─────────────────────────────────────────────────────────────────────

        public async Task<(bool Ok, string Details)> VerifyAccessAsync()
        {
            if (string.IsNullOrWhiteSpace(_settings.PageAccessToken))
            {
                return (false, "токен пустой");
            }

            if (string.IsNullOrWhiteSpace(_settings.PageId))
            {
                return (false, "PageId не заполнен");
            }

            try
            {
                // С маркером СТРАНИЦЫ запрос /me возвращает саму Страницу.
                // Если вернулся человек — значит, в конфиг попал пользовательский
                // маркер. Он выглядит точно так же и даже проходит отладчик
                // токенов, но публиковать Reels им нельзя, и ошибка при этом
                // приходит невнятная («Permissions error»). Поэтому проверяем
                // прямо здесь.
                var meUrl = $"{GraphBase}/me?fields=id,name&access_token={Uri.EscapeDataString(_settings.PageAccessToken)}";
                using var meResponse = await _httpClient.GetAsync(meUrl);
                var meText = await meResponse.Content.ReadAsStringAsync();

                if (!meResponse.IsSuccessStatusCode)
                {
                    return (false, DescribeError(meText, meResponse));
                }

                var me = JObject.Parse(meText);
                var id = me["id"]?.ToString() ?? string.Empty;
                var name = me["name"]?.ToString() ?? "—";

                if (id != _settings.PageId)
                {
                    return (false,
                        $"токен принадлежит «{name}» (id {id}), а в настройках PageId = {_settings.PageId}. " +
                        "Похоже, вставлен маркер пользователя, а нужен маркер Страницы");
                }

                // Чтение ленты Reels — проверка, что права на Страницу реально есть.
                var feedUrl = $"{GraphBase}/{_settings.PageId}/video_reels?limit=1&access_token={Uri.EscapeDataString(_settings.PageAccessToken)}";
                using var feedResponse = await _httpClient.GetAsync(feedUrl);
                var feedText = await feedResponse.Content.ReadAsStringAsync();

                if (!feedResponse.IsSuccessStatusCode)
                {
                    return (false, $"Страница «{name}» найдена, но Reels не читаются: {DescribeError(feedText, feedResponse)}");
                }

                return (true, $"Страница «{name}» (id {id})");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Разбор ошибок Graph API
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Превращает ответ Graph API в строку, по которой понятно, что чинить.
        /// Голый JSON от Facebook читать тяжело, а коды ошибок у Reels
        /// говорящие — расшифровываем самые частые.
        /// </summary>
        private string DescribeError(string body, HttpResponseMessage response)
        {
            try
            {
                var error = JObject.Parse(body)["error"] as JObject;
                if (error == null)
                {
                    return $"HTTP {(int)response.StatusCode}: {Trim(body)}";
                }

                var code = (int?)error["code"] ?? 0;
                _lastErrorCode = code;
                var subcode = (int?)error["error_subcode"] ?? 0;
                var message = error["message"]?.ToString() ?? "без текста";
                var userMessage = error["error_user_msg"]?.ToString();

                var hint = Hint(code, subcode);
                var parts = new List<string> { $"код {code}" };
                if (subcode != 0) parts.Add($"подкод {subcode}");
                parts.Add(message);
                if (!string.IsNullOrWhiteSpace(userMessage)) parts.Add(userMessage!);
                if (hint != null) parts.Add($"→ {hint}");

                return string.Join(" | ", parts);
            }
            catch
            {
                return $"HTTP {(int)response.StatusCode}: {Trim(body)}";
            }
        }

        /// <summary>Подсказка «что делать» для документированных кодов Reels API.</summary>
        private static string? Hint(int code, int subcode) => code switch
        {
            100 when subcode == 33 =>
                "такого объекта нет или у токена нет к нему доступа. Проверь PageId и что токен — от Страницы",
            100 =>
                "в запросе нет обязательного параметра либо он неверный. Проверь PageId и версию API",
            190 =>
                "маркер доступа недействителен или просрочен. Получи новый Page Access Token",
            200 =>
                "не хватает прав. Нужны pages_show_list, pages_read_engagement, pages_manage_posts " +
                "и право CREATE_CONTENT на Странице",
            368 =>
                "Facebook счёл действие злоупотреблением. Сбавь темп публикаций и проверь контент",
            613 =>
                "превышен лимит обращений. У Reels API потолок — 30 публикаций на Страницу " +
                "за скользящие 24 часа",
            80001 =>
                "слишком много запросов к этой Странице. Подожди и повтори",
            6000 =>
                "Facebook не смог принять видеофайл. Чаще всего дело в формате: нужен mp4, H.264/H.265, " +
                "AAC, 9:16, от 540x960, 3–90 секунд, 24–60 к/с",
            1363040 =>
                "соотношение сторон не поддерживается: должно быть между 16:9 и 9:16",
            1363127 =>
                "разрешение слишком низкое: минимум 540x960, рекомендуется 1080x1920",
            1363128 =>
                "длительность вне диапазона: Reels должен быть от 3 до 90 секунд",
            1363129 =>
                "частота кадров вне диапазона: нужно от 24 до 60 к/с",
            _ => null
        };

        private static string Trim(string s) =>
            s.Length > 500 ? s[..500] + "…" : s;
    }
}
