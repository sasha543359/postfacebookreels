using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;
using FacebookReelsPublisher.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FacebookReelsPublisher.Services
{
    public class TikTokMonitorService : ITikTokMonitorService
    {
        private readonly ILogger<TikTokMonitorService> _logger;
        private readonly TikTokMonitorSettings _settings;
        private readonly string _historyFile;
        private Dictionary<string, TikTokAccountHistory> _accountHistories = new();

        private const int MaxVideosPerCheck = 5;

        /// <summary>
        /// Сколько раз перезапускать yt-dlp, прежде чем уйти на прямую ссылку.
        /// Пять, а не три: извлечение проходит примерно в половине запусков, и на
        /// трёх попытках каждый восьмой ролик уезжал бы в 576x1024 без нужды.
        /// </summary>
        private const int MaxDownloadAttempts = 5;

        /// <summary>
        /// Код embed-страницы «такого автора нет». Приходит вместе с HTTP 400 и означает
        /// именно смену ника, удаление или бан: тот же код отдаётся на заведомо
        /// несуществующий ник, тогда как живой аккаунт отвечает 200 без errorCode.
        /// </summary>
        private const int EmbedUserNotFound = 10221;

        // Состояние embed-страницы лежит в <script id="__FRONTITY_CONNECT_STATE__">…</script>
        private static readonly Regex FrontityStateRegex = new(
            @"<script[^>]+id=[""']__FRONTITY_CONNECT_STATE__[""'][^>]*>(.*?)</script>",
            RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly HttpClient _http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All
            })
            {
                Timeout = TimeSpan.FromSeconds(60)
            };

            // Embed отдаётся и без этого, но с браузерным UA меньше шансов попасть
            // под очередное закручивание гаек.
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

            return client;
        }

        public TikTokMonitorService(
            ILogger<TikTokMonitorService> logger,
            IOptions<TikTokMonitorSettings> settings)
        {
            _logger = logger;
            _settings = settings.Value;

            if (!Directory.Exists(_settings.DownloadPath))
            {
                Directory.CreateDirectory(_settings.DownloadPath);
            }

            _historyFile = ResolveHistoryPath(_settings);

            LoadHistory();
        }

        /// <summary>
        /// Ник в том виде, в каком его понимает TikTok. Список авторов правят
        /// руками, и туда попадает что угодно: «@ник», ссылка на профиль, ник с
        /// пробелом по краям. Отдельная строка — «ник:7562467187447120952»:
        /// так в конфиг переехала подсказка из ошибки yt-dlp
        /// («try using "tiktokuser:channel_id"»). Любой из этих видов ломает
        /// адрес embed-страницы, и выглядит это в логах как «аккаунта нет».
        /// Пустая строка означает «ник разобрать не во что».
        /// </summary>
        public static string NormalizeUsername(string? raw)
        {
            var value = (raw ?? string.Empty).Trim();
            if (value.Length == 0) return string.Empty;

            // Ссылка на профиль: https://www.tiktok.com/@user?lang=ru
            var host = value.IndexOf("tiktok.com/", StringComparison.OrdinalIgnoreCase);
            if (host >= 0) value = value[(host + "tiktok.com/".Length)..];

            value = value.TrimStart('@').Trim();

            // Голый channel_id разбирать не во что: embed открывается только по нику.
            if (value.StartsWith("tiktokuser:", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            var cut = value.IndexOfAny(new[] { '/', '?', '#', ':', ' ', '\t' });
            if (cut >= 0) value = value[..cut];

            // Регистр обязателен. Сам TikTok к нему безразличен, а embed — нет:
            // «@Kryzamanaa» отвечает тем же «автора нет», что и выдуманный ник,
            // хотя «@kryzamanaa» жив. Без приведения к нижнему регистру одна
            // заглавная буква в конфиге навсегда хоронила бы живой источник.
            return value.Trim().ToLowerInvariant();
        }

        /// <summary>
        /// Куда класть историю. По умолчанию — рядом с видео, потому что в
        /// docker-compose в том вынесен только DownloadPath. Раньше файл лежал в
        /// рабочем каталоге контейнера и пропадал при каждой пересборке образа:
        /// история обнулялась, программа считала это первым запуском и молчала,
        /// пока у авторов не выйдет что-то новое. Снаружи это выглядело так,
        /// будто обновление сломало публикацию.
        /// </summary>
        private static string ResolveHistoryPath(TikTokMonitorSettings settings)
        {
            const string fileName = "video_history.json";

            var path = !string.IsNullOrWhiteSpace(settings.HistoryPath)
                ? settings.HistoryPath
                : !string.IsNullOrWhiteSpace(settings.DownloadPath)
                    ? Path.Combine(settings.DownloadPath, fileName)
                    : fileName;

            // Уже накопленную историю переносим один раз, чтобы обновление не
            // выглядело как сброс: иначе всё, что уже опубликовано, стало бы
            // «новым» и часть роликов ушла бы в Facebook по второму разу.
            if (!File.Exists(path) && File.Exists(fileName)
                && !string.Equals(Path.GetFullPath(path), Path.GetFullPath(fileName), StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Copy(fileName, path);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            return path;
        }

        public async Task<List<TikTokVideo>> GetLatestVideos(string username)
        {
            // TikTok закрыл WAF'ом оба места, куда ходил yt-dlp --flat-playlist:
            // /api/creator/item_list/ отвечает HTML-заглушкой "Site Maintenance"
            // вместо JSON, а страницу профиля через раз подменяет капчей. Отсюда и
            // "Unable to extract secondary user ID" в логах.
            // Embed-страница те же ролики отдаёт обычным GET — без impersonation,
            // ключей и подписей, поэтому теперь список берём оттуда, а yt-dlp
            // оставлен запасным путём.
            try
            {
                var feed = await GetLatestVideosFromEmbed(username);

                // «Такого автора нет» — это ответ, а не сбой связи. Идти за ним
                // в yt-dlp бессмысленно: на ленте автора он мёртв и вернёт своё
                // «Unable to extract secondary user ID», из-за которого мёртвый
                // ник в логах выглядит как поломка всей программы.
                if (feed.UserNotFound)
                {
                    _logger.LogWarning(
                        $"   ⛔ TikTok не знает @{username}: аккаунт удалён, забанен или сменил ник. " +
                        "Поправьте TikTokUsernames в appsettings.json — сам он не вернётся.");
                    return new List<TikTokVideo>();
                }

                if (feed.Videos.Count > 0)
                {
                    return feed.Videos;
                }

                _logger.LogWarning($"   ⚠️  Embed @{username} не отдал ни одного ролика — пробуем yt-dlp");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"   ⚠️  Embed @{username} недоступен ({ex.Message}) — пробуем yt-dlp");
            }

            return await GetLatestVideosFromYtDlp(username);
        }

        /// <summary>
        /// Список последних роликов из https://www.tiktok.com/embed/@user.
        /// В HTML вшито состояние Frontity, где лежит videoList: id, описание
        /// и прямая ссылка на mp4.
        /// </summary>
        private async Task<EmbedFeed> GetLatestVideosFromEmbed(string username)
        {
            // Ник приводится к нижнему регистру и здесь тоже: сюда можно попасть
            // в обход нормализации при старте, а embed различает регистр.
            using var response = await _http.GetAsync(
                $"https://www.tiktok.com/embed/@{username.ToLowerInvariant()}");

            // Тело читаем при ЛЮБОМ коде ответа. «Такого автора нет» приходит с
            // HTTP 400, но страница при этом полноценная: признак лежит внутри,
            // в errorCode. GetStringAsync выбросил бы исключение вместе с телом,
            // и отличить удалённый аккаунт от временной блокировки стало бы нечем.
            var html = await response.Content.ReadAsStringAsync();

            var match = FrontityStateRegex.Match(html);
            if (!match.Success)
            {
                throw new Exception($"в ответе нет __FRONTITY_CONNECT_STATE__ (HTTP {(int)response.StatusCode})");
            }

            var data = JObject.Parse(match.Groups[1].Value)["source"]?["data"] as JObject;

            // Имя узла — это сам путь, то есть "/embed/@username"
            var node = data?.Properties()
                .FirstOrDefault(p => p.Name.StartsWith("/embed/", StringComparison.Ordinal))
                ?.Value as JObject;

            if (node == null)
            {
                throw new Exception($"в состоянии страницы нет узла /embed/ (HTTP {(int)response.StatusCode})");
            }

            var errorCode = (int?)node["errorCode"] ?? 0;
            if (errorCode == EmbedUserNotFound)
            {
                return new EmbedFeed(new List<TikTokVideo>(), UserNotFound: true);
            }

            if (errorCode != 0)
            {
                throw new Exception($"embed вернул errorCode {errorCode} (HTTP {(int)response.StatusCode})");
            }

            var videoList = node["videoList"] as JArray;
            if (videoList == null)
            {
                throw new Exception("в состоянии страницы нет videoList " +
                                    "(аккаунт приватный или встраивание отключено)");
            }

            var videos = new List<TikTokVideo>();
            var skippedPhotos = 0;

            foreach (var item in videoList.OfType<JObject>())
            {
                var videoId = item["id"]?.ToString();
                if (string.IsNullOrWhiteSpace(videoId) || !long.TryParse(videoId, out var numericId))
                {
                    continue;
                }

                var playAddr = item["playAddr"]?.ToString() ?? string.Empty;

                // Пустой playAddr — это не ролик, а фото-пост (слайдшоу). Проверено:
                // на 273 записях ленты пустых оказалось две, и обе такие. Публиковать
                // их нечем, а yt-dlp по такому адресу отдаёт mp3 с расширением mp4 —
                // Facebook принял бы «ролик» без картинки.
                if (playAddr.Length == 0)
                {
                    skippedPhotos++;
                    continue;
                }

                // Старшие 32 бита ID ролика TikTok — unix-время публикации. С timestamp
                // от yt-dlp расходится на несколько секунд, но для сравнения
                // "новее / старее" этого достаточно, а лишний запрос не нужен.
                var timestamp = numericId >> 32;

                videos.Add(new TikTokVideo
                {
                    Id = videoId,
                    Title = item["desc"]?.ToString() ?? string.Empty,
                    UploadDate = DateTimeOffset.FromUnixTimeSeconds(timestamp).ToString("yyyyMMdd"),
                    Duration = 0,
                    DurationUnknown = true, // embed длительность не отдаёт
                    Timestamp = timestamp,
                    PlayAddr = playAddr,
                    Url = $"https://www.tiktok.com/@{username}/video/{videoId}"
                });
            }

            if (skippedPhotos > 0)
            {
                _logger.LogInformation($"   Пропущено фото-постов у @{username}: {skippedPhotos}");
            }

            // Embed перечисляет ролики вперемешку (закреплённые не по дате),
            // поэтому порядок наводим сами — раньше это делал --playlist-end.
            return new EmbedFeed(
                videos
                    .OrderByDescending(v => v.Timestamp)
                    .Take(MaxVideosPerCheck)
                    .ToList(),
                UserNotFound: false);
        }

        /// <summary>Что ответила embed-страница: список роликов либо «автора нет».</summary>
        private sealed record EmbedFeed(List<TikTokVideo> Videos, bool UserNotFound);

        private async Task<List<TikTokVideo>> GetLatestVideosFromYtDlp(string username)
        {
            try
            {
                var url = $"https://www.tiktok.com/@{username}";

                // Получаем последние 5 видео с ID, title, upload_date, duration и timestamp
                var args = $"--flat-playlist " +
                           $"--print \"%(id)s|%(title)s|%(upload_date)s|%(duration)s|%(timestamp)s\" " +
                           $"--playlist-end {MaxVideosPerCheck} " +
                           Reliability +
                           ExtraArgs() +
                           $"\"{url}\"";

                var output = await RunProcessAsync(_settings.YtDlpPath, args);

                var videos = new List<TikTokVideo>();

                foreach (var line in output.Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var parts = line.Split('|');
                    if (parts.Length >= 5)
                    {
                        var videoId = parts[0].Trim();
                        var title = parts[1].Trim();
                        var uploadDate = parts[2].Trim();
                        var durationStr = parts[3].Trim();
                        var timestampStr = parts[4].Trim();

                        int.TryParse(durationStr, out int duration);
                        long.TryParse(timestampStr, out long timestamp);

                        videos.Add(new TikTokVideo
                        {
                            Id = videoId,
                            Title = title,
                            UploadDate = uploadDate,
                            Duration = duration,
                            Timestamp = timestamp,
                            Url = $"https://www.tiktok.com/@{username}/video/{videoId}"
                        });
                    }
                }

                return videos;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Ошибка получения видео с @{username}");
                return new List<TikTokVideo>();
            }
        }

        public async Task<TikTokVideo?> CheckForNewVideo(string username, string historyKey)
        {
            try
            {
                _logger.LogInformation($"   Проверяем новые видео у @{username} (история: {historyKey})");

                var videos = await GetLatestVideos(username);

                var videoList = videos.Where(v => v.IsVideo).ToList();

                if (videoList.Count == 0)
                {
                    _logger.LogInformation("   Видео не найдены (аккаунт может не существовать или изменил ник)");
                    return null;
                }

                // Если ключа нет в истории - первый запуск для этой пары
                if (!_accountHistories.ContainsKey(historyKey))
                {
                    _logger.LogInformation($"   🆕 Первый запуск для {historyKey}");
                    _logger.LogInformation($"   Инициализируем историю с последними {videoList.Count} видео");

                    var history = new TikTokAccountHistory();
                    foreach (var video in videoList)
                    {
                        history.AddVideo(video.Id, video.Timestamp);
                        _logger.LogInformation($"      - {video.Id} (timestamp: {video.Timestamp})");
                    }

                    _accountHistories[historyKey] = history;
                    SaveHistory();

                    _logger.LogInformation($"   ✅ История инициализирована, следующие видео будут публиковаться");
                    return null; // НЕ публикуем при первом запуске
                }

                var accountHistory = _accountHistories[historyKey];
                var latestTimestamp = accountHistory.GetLatestTimestamp();

                // Проверяем каждое видео
                foreach (var video in videoList)
                {
                    // Условия для публикации:
                    // 1. ID НЕТ в истории
                    // 2. Timestamp БОЛЬШЕ самого свежего в истории (защита от старых видео)
                    if (!accountHistory.ContainsVideo(video.Id) && video.Timestamp > latestTimestamp)
                    {
                        _logger.LogInformation($"   🎉 Найдено НОВОЕ видео!");
                        _logger.LogInformation($"      Название: {video.Title}");
                        _logger.LogInformation($"      ID: {video.Id}");
                        _logger.LogInformation($"      Timestamp: {video.Timestamp} (последний в истории: {latestTimestamp})");

                        return video;
                    }
                }

                _logger.LogInformation($"   Нет новых видео (последнее: {videoList.First().Id})");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"   ❌ Ошибка проверки @{username}: {ex.Message}");
                _logger.LogInformation("   Пропускаем этот аккаунт и продолжаем...");
                return null;
            }
        }

        public void MarkVideoAsProcessed(string historyKey, string videoId, long timestamp)
        {
            try
            {
                _logger.LogInformation($"   ✅ Сохраняем видео {videoId} в историю [{historyKey}]");

                if (!_accountHistories.ContainsKey(historyKey))
                {
                    _accountHistories[historyKey] = new TikTokAccountHistory();
                }

                _accountHistories[historyKey].AddVideo(videoId, timestamp);
                SaveHistory();

                _logger.LogInformation($"   История обновлена. Всего видео в истории: {_accountHistories[historyKey].Videos.Count}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"   ❌ Ошибка сохранения истории для [{historyKey}]");
            }
        }

        public async Task<string> DownloadVideo(TikTokVideo video, string? customPath = null)
        {
            var outputPath = customPath ?? Path.Combine(_settings.DownloadPath, $"{video.Id}.mp4");

            _logger.LogInformation($"   📥 Скачиваем видео: {video.Title}");
            _logger.LogInformation($"   URL: {video.Url}");

            // Два пути, и они дают РАЗНОЕ, поэтому порядок именно такой.
            //
            // yt-dlp добирается до мастера 1080x1920: у ролика есть отдельные
            // дорожки bytevc1_1080p (h265, «video only») плюс звук, и «-S res,br»
            // выбирает их и склеивает. Прямая ссылка из embed — это готовый
            // muxed-вариант h264_540p, то есть 576x1024. Замер 21.08.2026 на
            // четырёх авторах: playAddr всякий раз 576x1024, yt-dlp всякий раз
            // 1080x1920.
            //
            // Для Facebook разница между этими двумя путями серьёзнее, чем была
            // для Instagram. Минимум Reels API — 540x960, а прямая ссылка даёт
            // 576x1024: проходит, но впритык, и любой кроп уникализатора может
            // увести ролик под планку. Тогда Facebook отвечает ошибкой 1363127
            // («разрешение слишком низкое»). С 1080x1920 запаса хватает на всё.
            //
            // Зато embed отвечает всегда, а yt-dlp — два-четыре раза из шести
            // («Unable to extract universal data for rehydration»). Поэтому он
            // запасной: лучше опубликовать 540p, чем пропустить ролик.
            //
            // Водяной знак ни одному из путей не грозит: формат «download»
            // (единственный с меткой) экстрактор помечает отрицательным
            // приоритетом, а он применяется раньше пользовательской сортировки;
            // playAddr — это то, что играет сам сайт.
            //
            // «--postprocessor-args Merger:…» не украшение. Звук у части роликов
            // существует только как mp3, и склейка отдала бы mp4 с mp3 внутри —
            // Reels требует AAC. «-ar 48000 -ac 2» там же по той же причине:
            // Facebook в спецификации Reels просит именно 48 кГц и стерео, а
            // TikTok нередко отдаёт 44,1 кГц и моно.
            var args = $"-o \"{outputPath}\" " +
                       $"-S res,br " +
                       $"--no-playlist " +
                       $"--no-part " +
                       $"--merge-output-format mp4 " +
                       $"--postprocessor-args \"Merger:-c:v copy -c:a aac -b:a 160k -ar 48000 -ac 2\" " +
                       Reliability +
                       ExtraArgs() +
                       $"\"{video.Url}\"";

            Exception? lastError = null;

            // Повторяется именно ЗАПУСК ПРОЦЕССА. Флаг --retries тут бесполезен:
            // он про докачку файла, а падает извлечение — до скачивания дело не
            // доходит вовсе, и внутри одного запуска повторять нечего.
            for (var attempt = 1; attempt <= MaxDownloadAttempts; attempt++)
            {
                TryDelete(outputPath);

                try
                {
                    await RunProcessAsync(_settings.YtDlpPath, args, showProgress: false);

                    if (!File.Exists(outputPath))
                    {
                        throw new Exception("yt-dlp отчитался успехом, но файла нет");
                    }

                    _logger.LogInformation($"   ✅ Видео скачано: {Path.GetFileName(outputPath)}");
                    return outputPath;
                }
                catch (Exception ex)
                {
                    lastError = ex;

                    var reason = ex.Message
                        .Split('\n')
                        .LastOrDefault(l => !string.IsNullOrWhiteSpace(l))
                        ?.Trim() ?? ex.Message;

                    _logger.LogWarning($"   ⚠️  yt-dlp, попытка {attempt} из {MaxDownloadAttempts}: {reason}");

                    if (attempt < MaxDownloadAttempts)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5));
                    }
                }
            }

            TryDelete(outputPath);

            if (string.IsNullOrEmpty(video.PlayAddr))
            {
                _logger.LogError(lastError, $"   ❌ Ошибка скачивания видео: {video.Url}");
                throw lastError ?? new Exception($"не удалось скачать {video.Url}");
            }

            _logger.LogWarning("   ⚠️  yt-dlp не справился — берём прямую ссылку из embed (576x1024 вместо 1080x1920)");

            try
            {
                await DownloadDirectAsync(video.PlayAddr, outputPath);

                _logger.LogInformation($"   ✅ Видео скачано напрямую из embed: {Path.GetFileName(outputPath)}");
                return outputPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"   ❌ Ошибка скачивания видео: {video.Url}");
                throw;
            }
        }

        /// <summary>
        /// Флаги устойчивости, одинаковые для чтения ленты и для скачивания.
        ///
        /// Каждый стоит здесь по конкретной причине, а не «на всякий случай»:
        ///
        /// --user-agent  Обходной путь для блокировки, описанной в yt-dlp #17604.
        ///               TikTok режет отпечаток chrome-150, который curl-cffi
        ///               выбирает сам, если версия библиотеки свежее 0.16.0.
        ///               Свой User-Agent переживает подстановку заголовков
        ///               curl-cffi и снимает блокировку. Вторая половина лечения —
        ///               закреплённая версия curl-cffi в Dockerfile.
        ///
        /// --extractor-retries  По умолчанию их всего 3. Собственные тесты yt-dlp
        ///               для лент TikTok выставляют 10, потому что лента отдаётся
        ///               с перебоями by design.
        ///
        /// -R / --retry-sleep  Экспоненциальная пауза вместо частых повторов:
        ///               при блокировке по IP частые повторы только усугубляют.
        ///
        /// --sleep-requests  TikTok считает запросы по IP. Пауза нужна особенно
        ///               на сервере: дата-центровые адреса блокируют охотнее, чем
        ///               домашние.
        ///
        /// --socket-timeout  Без него зависшее соединение держит процесс до
        ///               упора, и цикл проверки встаёт целиком.
        /// </summary>
        private const string Reliability =
            "--user-agent \"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/145.0.0.0 Safari/537.36\" " +
            "--extractor-retries 10 " +
            "-R 10 " +
            "--retry-sleep \"extractor:exp=1:30\" " +
            "--retry-sleep \"http:exp=1:30\" " +
            "--sleep-requests 1.5 " +
            "--socket-timeout 30 ";

        /// <summary>
        /// Хвост аргументов yt-dlp из настроек, одинаковый для чтения ленты и
        /// для скачивания.
        ///
        /// Зачем это вынесено в конфиг. TikTok ломает извлечение регулярно, и
        /// починка со стороны yt-dlp почти всегда сводится к одному новому
        /// флагу — сменившемуся хосту API или подмене отпечатка браузера. Без
        /// этого места такой флаг пришлось бы дописывать в исходники и
        /// пересобирать образ; здесь достаточно правки appsettings.json.
        ///
        /// Пустые значения ничего не добавляют, так что по умолчанию команда
        /// ровно та же, что была.
        /// </summary>
        private string ExtraArgs()
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(_settings.Impersonate))
            {
                parts.Add($"--impersonate \"{_settings.Impersonate.Trim()}\"");
            }

            if (!string.IsNullOrWhiteSpace(_settings.ExtraYtDlpArgs))
            {
                parts.Add(_settings.ExtraYtDlpArgs.Trim());
            }

            return parts.Count == 0 ? string.Empty : string.Join(" ", parts) + " ";
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

        private static async Task DownloadDirectAsync(string playAddr, string outputPath)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, playAddr);
            request.Headers.TryAddWithoutValidation("Referer", "https://www.tiktok.com/");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var expected = response.Content.Headers.ContentLength;

            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var target = File.Create(outputPath))
            {
                await source.CopyToAsync(target);
            }

            // Заглушки и страницы с ошибкой весят единицы килобайт — отсекаем их,
            // чтобы дальше по конвейеру не ушёл битый файл.
            var size = new FileInfo(outputPath).Length;
            if (size < 10 * 1024)
            {
                TryDelete(outputPath);
                throw new Exception($"вместо видео пришло {size} байт");
            }

            // Оборванная закачка проходит проверку по размеру и уходит дальше как
            // целый файл: Facebook примет контейнер, а ролик оборвётся на середине.
            // CDN отдаёт длину заранее — сверяем.
            if (expected.HasValue && size != expected.Value)
            {
                TryDelete(outputPath);
                throw new Exception($"скачано {size} байт из {expected.Value}");
            }
        }

        private void LoadHistory()
        {
            try
            {
                if (File.Exists(_historyFile))
                {
                    var json = File.ReadAllText(_historyFile);
                    _accountHistories = JsonConvert.DeserializeObject<Dictionary<string, TikTokAccountHistory>>(json)
                                       ?? new Dictionary<string, TikTokAccountHistory>();

                    var totalVideos = _accountHistories.Sum(h => h.Value.Videos.Count);
                    _logger.LogInformation($"📂 Загружена история: {_accountHistories.Count} аккаунтов, {totalVideos} видео");

                    // Показываем историю для каждого аккаунта
                    foreach (var kvp in _accountHistories)
                    {
                        _logger.LogInformation($"   @{kvp.Key}: {kvp.Value.Videos.Count} видео в истории");
                    }
                }
                else
                {
                    _logger.LogWarning($"⚠️  Файл истории не найден: {_historyFile}");
                    _logger.LogInformation("🆕 Первый запуск - будет создан новый файл");
                    _accountHistories = new Dictionary<string, TikTokAccountHistory>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Не удалось загрузить историю");
                _accountHistories = new Dictionary<string, TikTokAccountHistory>();
            }
        }

        private void SaveHistory()
        {
            try
            {
                var json = JsonConvert.SerializeObject(_accountHistories, Formatting.Indented);
                File.WriteAllText(_historyFile, json);
                _logger.LogDebug($"Файл {_historyFile} обновлён");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Ошибка сохранения истории");
            }
        }

        private async Task<string> RunProcessAsync(string fileName, string arguments, bool showProgress = false)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = !showProgress,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            using var process = new Process { StartInfo = startInfo };

            var outputBuilder = new System.Text.StringBuilder();
            var errorBuilder = new System.Text.StringBuilder();

            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    outputBuilder.AppendLine(e.Data);
                    if (showProgress)
                    {
                        Console.WriteLine(e.Data);
                    }
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    errorBuilder.AppendLine(e.Data);
                    if (showProgress)
                    {
                        Console.WriteLine(e.Data);
                    }
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            var output = outputBuilder.ToString();
            var error = errorBuilder.ToString();

            if (process.ExitCode != 0)
            {
                throw new Exception($"yt-dlp завершился с ошибкой (код {process.ExitCode}):\n{error}");
            }

            return output;
        }
    }
}