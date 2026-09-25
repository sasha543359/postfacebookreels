using System.Collections.Generic;

namespace FacebookReelsPublisher.Models
{
    /// <summary>
    /// Одна Страница Facebook, куда публикуем Reels.
    ///
    /// Именно СТРАНИЦА, а не личный профиль: Reels Publishing API умеет
    /// публиковать только на Страницы. Дословно из документации Meta:
    /// «Видео Reels можно публиковать только на Страницах Facebook».
    /// Личный профиль через API не поддерживается вообще — это ограничение
    /// платформы, а не программы.
    /// </summary>
    public class FacebookPageSettings
    {
        /// <summary>Понятное имя — только для логов и для счётчика анти-спама.</summary>
        public string PageName { get; set; } = string.Empty;

        /// <summary>
        /// Числовой ID Страницы. Берётся из GET /me/accounts или со страницы
        /// «О себе» → «Подробности». Не путать с ID личного профиля.
        /// </summary>
        public string PageId { get; set; } = string.Empty;

        /// <summary>
        /// Маркер доступа к Странице (Page Access Token), а НЕ пользовательский.
        /// У его владельца должно быть право CREATE_CONTENT на этой Странице,
        /// а у приложения — разрешения pages_show_list, pages_read_engagement,
        /// pages_manage_posts.
        ///
        /// Пусто или заглушка из шаблона («ВСТАВЬ_…») — Страница пропускается
        /// (см. NotReadyReason).
        /// </summary>
        public string PageAccessToken { get; set; } = string.Empty;

        /// <summary>Ники TikTok, за которыми следим для этой Страницы. Без «@».</summary>
        public List<string> TikTokUsernames { get; set; } = new List<string>();

        /// <summary>Описание под Reels. Пусто — берётся описание исходного ролика TikTok.</summary>
        public string? CustomCaption { get; set; }

        /// <summary>
        /// Обложка Reels: ТОЛЬКО имя файла из папки обложек (напр. "cover.jpg").
        /// Пусто — Facebook сам возьмёт кадр из видео.
        ///
        /// Устроено иначе, чем в Instagram-версии: там обложка передавалась
        /// ссылкой (cover_url), а Facebook принимает её только файлом, отдельным
        /// запросом POST /{video_id}/thumbnails. Поэтому здесь нужен локальный
        /// файл, а не URL.
        /// </summary>
        public string? CoverImage { get; set; }

        /// <summary>
        /// Профиль уникализатора ("acc1".."acc10"). Даёт этой Странице свою
        /// стабильную сигнатуру (кроп, цвет, скорость, питч), чтобы один и тот же
        /// исходник на разных Страницах превращался в разные файлы.
        /// Пусто — берётся PageName (тоже уникальный seed).
        /// </summary>
        public string? UniquifierProfile { get; set; }

        /// <summary>Экранная подпись (материальная трансформация). Пусто — без подписи.</summary>
        public string? OnScreenCaption { get; set; }

        /// <summary>
        /// Почему Страница не может публиковать, или null, если может.
        ///
        /// Заглушка из шаблона («ВСТАВЬ_…») считается незаполненным полем, как
        /// пустая строка. Раньше программа принимала её за настоящий токен, и
        /// оставленный в шаблоне блок второй Страницы каждые пять минут
        /// отбивался ошибкой авторизации — поэтому в шаблоне у неё стояли
        /// пустые строки, и было непонятно, куда что вставлять.
        /// </summary>
        public string? NotReadyReason =>
            IsBlank(PageAccessToken) ? "не вставлен токен (PageAccessToken)"
            : IsBlank(PageId) ? "не вставлен ID Страницы (PageId)"
            : null;

        private static bool IsBlank(string? value) =>
            string.IsNullOrWhiteSpace(value)
            || value.TrimStart().StartsWith("ВСТАВЬ", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Обязательная строка этой Страницы (первая строка описания) вместо
        /// общей AppSettings.RequiredCaptionSuffix — например, другой
        /// Twitch-канал. Пусто — берётся общая: так Страница без своей строки
        /// всё равно не останется без обязательной подписи.
        /// </summary>
        public string? RequiredCaptionSuffix { get; set; }

        /// <summary>
        /// Хэштеги этой Страницы вместо общих AppSettings.DefaultHashtags.
        /// Нужны, когда Страницы на разных языках: общие хэштеги русские, а
        /// англоязычной Странице «#мемы #приколы» вредят — по языку подписи
        /// Facebook решает, кому показать ролик. Дописываются по тому же
        /// правилу: только если в тексте ролика своих хэштегов нет.
        /// Пусто — берутся общие.
        /// </summary>
        public string? DefaultHashtags { get; set; }
    }

    public class AppSettings
    {
        public List<FacebookPageSettings> FacebookPages { get; set; } = new();
        public int CheckIntervalMinutes { get; set; } = 10;
        public int TikTokCheckDelaySeconds { get; set; } = 30;

        /// <summary>
        /// Строка, которая обязана быть в описании КАЖДОГО Reels. Дописывается в
        /// конец описания автоматически — и к тексту исходного ролика TikTok, и к
        /// CustomCaption, если он задан.
        ///
        /// Сделано отдельной настройкой, а не вписано в CustomCaption у каждой
        /// Страницы, ровно по одной причине: подпись обязательная. Впиши её
        /// руками в десять мест — и однажды она потеряется при правке одного из
        /// них, причём заметно это станет уже после публикации. У Страницы
        /// может быть своя (FacebookPageSettings.RequiredCaptionSuffix), но
        /// эта остаётся запасной: пустое поле у Страницы — берётся эта.
        ///
        /// Если строка уже есть в описании, второй раз не добавляется.
        /// Пусто — ничего не дописывается.
        /// </summary>
        public string RequiredCaptionSuffix { get; set; } = "Twitch: MELLSTROY";

        /// <summary>
        /// Хэштеги, которые дописываются, если в тексте ролика своих нет.
        /// По умолчанию русские: аудитория — русскоязычная, а язык подписи и
        /// хэштегов — один из сигналов, по которым Facebook выбирает, кому
        /// показать ролик. Пусто — хэштеги не дописываются.
        /// </summary>
        public string DefaultHashtags { get; set; } = "#мемы #приколы #юмор #рилс";

        public UniquifierSettings Uniquifier { get; set; } = new();
        public PublishingSettings Publishing { get; set; } = new();
    }

    /// <summary>
    /// Анти-спам лимиты публикации на Страницу.
    /// </summary>
    public class PublishingSettings
    {
        /// <summary>
        /// Максимум Reels на одну Страницу за скользящие 24 часа. 0 = без лимита.
        ///
        /// Окно именно СКОЛЬЗЯЩЕЕ, а не «календарные сутки UTC», потому что сам
        /// Facebook считает так же: «не более 30 публикаций через API в течение
        /// скользящего 24-часового периода». При календарном счётчике можно было
        /// бы выложить 30 роликов в 23:50 и ещё 30 в 00:10 — по нашему счётчику
        /// это два разных дня, а по счётчику Facebook одно окно, и вторая пачка
        /// начала бы отбиваться ошибкой 613.
        /// </summary>
        public int MaxPostsPerPagePerDay { get; set; } = 8;

        /// <summary>Минимальный интервал между публикациями одной Страницы, минут. 0 = без интервала.</summary>
        public int MinMinutesBetweenPosts { get; set; } = 3;

        /// <summary>
        /// Файл счётчика. Лежит в папке видео, потому что она примонтирована
        /// с сервера и переживает пересборку образа.
        /// </summary>
        public string CountsPath { get; set; } = "/var/www/videos/post_counts.json";

        /// <summary>
        /// Жёсткий потолок самого Facebook: 30 публикаций на Страницу за скользящие
        /// 24 часа. Программа не даст его превысить, даже если в
        /// MaxPostsPerPagePerDay написать больше. Менять не нужно — это не наша
        /// настройка, а ограничение API; поле оставлено на случай, если Meta
        /// однажды поднимет планку.
        /// </summary>
        public int ApiHardLimitPer24h { get; set; } = 30;
    }

    /// <summary>
    /// Настройки уникализатора (вызывает python-скрипт uniquify.py через ffmpeg).
    /// </summary>
    public class UniquifierSettings
    {
        /// <summary>Включить обработку видео перед публикацией. false — публикуем сырьём.</summary>
        public bool Enabled { get; set; } = false;
        public string PythonPath { get; set; } = "python3";
        public string ScriptPath { get; set; } = "/app/uniquify.py";

        /// <summary>"crop" (кадр почти целиком, зум 2–3%) | "reframe" (клип на размытом фоне).</summary>
        public string Mode { get; set; } = "crop";

        /// <summary>"keep" | "strip" | "replace" | "mix".</summary>
        public string Audio { get; set; } = "keep";

        /// <summary>TTF со шрифтом с кириллицей (для --caption). На Linux-контейнере — DejaVu.</summary>
        public string Font { get; set; } = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf";

        /// <summary>Таймаут на обработку одного видео, секунды.</summary>
        public int TimeoutSeconds { get; set; } = 180;
    }

    public class ServerSettings
    {
        /// <summary>
        /// Публичный адрес сервера, по которому Facebook забирает видео,
        /// напр. "http://203.0.113.10". Должен быть доступен снаружи.
        /// </summary>
        public string PublicUrl { get; set; } = string.Empty;

        public string VideoPath { get; set; } = "/var/www/videos";

        /// <summary>Папка с обложками (локальный путь на сервере).</summary>
        public string CoverPath { get; set; } = "/var/www/videos/covers";

        /// <summary>
        /// Как отдавать видео Facebook.
        ///   "url"   — дать ссылку на наш nginx, Facebook скачает сам (как в Instagram-версии);
        ///   "bytes" — залить файл телом запроса, публичный адрес не нужен вообще;
        ///   "auto"  — попробовать ссылку; на байты переходит, только если Facebook
        ///             сразу отказался принять ссылку.
        /// По умолчанию "bytes": единственный режим, проверенный вживую
        /// (18.09.2026), и не зависит от того, виден ли сервер снаружи.
        /// </summary>
        public string UploadMode { get; set; } = "bytes";
    }

    /// <summary>
    /// Настройки одного «подключения» к Graph API. Собираются на лету
    /// под каждую Страницу из FacebookPageSettings.
    /// </summary>
    public class FacebookSettings
    {
        public string PageId { get; set; } = string.Empty;
        public string PageAccessToken { get; set; } = string.Empty;

        /// <summary>
        /// Версия Graph API. Меняется примерно раз в квартал; старые версии
        /// живут ~2 года, так что спешить с обновлением не надо.
        /// </summary>
        public string ApiVersion { get; set; } = "v26.0";

        /// <summary>Сколько секунд ждать, пока Facebook обработает видео.</summary>
        public int ProcessingTimeoutSeconds { get; set; } = 300;

        /// <summary>Режим отдачи файла: "url" | "bytes" | "auto".</summary>
        public string UploadMode { get; set; } = "bytes";
    }

    public class TikTokMonitorSettings
    {
        public string YtDlpPath { get; set; } = "/usr/local/bin/yt-dlp";
        public string DownloadPath { get; set; } = "/var/www/videos";

        /// <summary>
        /// Файл с историей опубликованных роликов. Пусто — кладём его рядом с
        /// видео, в DownloadPath: это единственный каталог, который в
        /// docker-compose вынесен в том. В рабочем каталоге контейнера файл жил
        /// бы ровно до пересборки образа, а потеря истории означает «первый
        /// запуск» — то есть молчание до тех пор, пока у каждого автора не
        /// выйдет новый ролик.
        /// </summary>
        public string HistoryPath { get; set; } = string.Empty;

        /// <summary>
        /// Дополнительные аргументы yt-dlp, добавляются к встроенным.
        /// Нужны как аварийный рычаг: TikTok ломает извлечение регулярно, и
        /// починка обычно сводится к одному новому флагу. Через этот параметр
        /// его можно добавить, не трогая код и не пересобирая образ заново
        /// из изменённых исходников.
        /// Пример: "--extractor-args tiktok:api_hostname=api22-normal-c-useast2a.tiktokv.com"
        /// </summary>
        public string ExtraYtDlpArgs { get; set; } = string.Empty;

        /// <summary>
        /// Цель для --impersonate (curl-cffi), напр. "chrome". Пусто — без
        /// подмены отпечатка. Помогает, когда TikTok начинает отдавать капчу
        /// вместо страницы.
        /// </summary>
        public string Impersonate { get; set; } = string.Empty;
    }

    /// <summary>Результат публикации Reels.</summary>
    public class PublishResult
    {
        public bool Success { get; set; }

        /// <summary>ID видео, полученный на шаге инициализации сеанса загрузки.</summary>
        public string? VideoId { get; set; }

        /// <summary>ID публикации, если Facebook его вернул (бывает не всегда).</summary>
        public string? PostId { get; set; }

        public string? ErrorMessage { get; set; }

        /// <summary>Код ошибки Graph API, если Facebook его прислал. 0 — кода не было.</summary>
        public int ErrorCode { get; set; }

        /// <summary>
        /// Facebook не примет ЭТОТ файл никогда: не то соотношение сторон,
        /// разрешение, длительность или частота кадров.
        ///
        /// Различать такие отказы от временных обязательно. Неопубликованный
        /// ролик не отмечается в истории и приходит на публикацию снова в каждом
        /// цикле. Для временной ошибки это правильно — со второго раза выйдет.
        /// Для негодного файла это вечный круг: каждые пять минут скачать,
        /// обработать, залить и получить тот же отказ, и так пока ролик не
        /// уедет из ленты автора.
        /// </summary>
        public bool IsPermanentlyRejected =>
            ErrorCode is 1363040 or 1363127 or 1363128 or 1363129
            || RejectedAfterFinish;

        /// <summary>
        /// finish прошёл, а потом Facebook сам отказал видео при обработке.
        /// Такой отказ приходит в статусе, часто без числового кода, и повторная
        /// заливка того же файла дала бы тот же отказ — поэтому он окончательный.
        /// </summary>
        public bool RejectedAfterFinish { get; set; }
    }

    /// <summary>
    /// Ответ GET /{video_id}?fields=status. Facebook рассказывает про три фазы
    /// отдельно, и это важно: «загрузка прошла» и «видео готово» — разные вещи,
    /// а ошибка про слишком низкое разрешение приходит именно в processing_phase.
    /// </summary>
    public class ReelStatus
    {
        /// <summary>"ready" | "processing" | "error" (или "" если ответ не разобрался).</summary>
        public string VideoStatus { get; set; } = string.Empty;

        public string UploadingPhase { get; set; } = string.Empty;
        public string ProcessingPhase { get; set; } = string.Empty;
        public string PublishingPhase { get; set; } = string.Empty;

        /// <summary>Сколько байт Facebook уже принял (для возобновления загрузки).</summary>
        public long BytesTransferred { get; set; }

        /// <summary>
        /// Ошибка, которую сообщил САМ Facebook: в одной из фаз видео. Только
        /// она означает, что с роликом что-то не так.
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// Код той же ошибки. Если Facebook кода не приложил (в документации
        /// Meta его и нет), он восстанавливается по тексту — см. CheckStatusAsync.
        /// </summary>
        public int ErrorCode { get; set; }

        /// <summary>
        /// Статус НЕ удалось прочитать: сеть, лимит запросов, 5xx. О самом видео
        /// это ничего не говорит, поэтому в IsError не входит — иначе один
        /// сорвавшийся опрос после finish выглядел бы как отказ, и ролик
        /// выложился бы повторно в следующем цикле.
        /// </summary>
        public string? RequestError { get; set; }

        public bool RequestFailed => !string.IsNullOrEmpty(RequestError);

        public bool IsReady => VideoStatus == "ready";

        // Кроме "error" Facebook отдаёт ещё "upload_failed" (файл не доехал) и
        // "expired" (сеанс загрузки протух). Ждать после любого из них нечего:
        // без этой проверки опрос статуса крутился бы до самого таймаута
        // впустую.
        public bool IsError =>
            VideoStatus is "error" or "upload_failed" or "expired"
            || !string.IsNullOrEmpty(ErrorMessage);

        /// <summary>
        /// Файл целиком у Facebook — можно звать фазу finish.
        ///
        /// Ждать до finish именно загрузку, а не обработку: проверено вживую
        /// 18.09.2026 — processing_phase стоит в "not_started", пока не вызван
        /// finish, и не сдвинется, сколько ни жди. Ожидание обработки до finish
        /// съедало на каждой публикации весь таймаут (5 минут) впустую.
        /// </summary>
        public bool IsUploaded => UploadingPhase == "complete";

        /// <summary>Facebook закончил публикацию после finish.</summary>
        public bool IsPublished => PublishingPhase == "complete";
    }

    public class VideoPublishInfo
    {
        /// <summary>Локальный путь к mp4 (нужен для режима "bytes" и как запасной путь).</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>Публичная ссылка на mp4 (нужна для режима "url").</summary>
        public string? VideoUrl { get; set; }

        /// <summary>Описание под Reels. Поддерживает хэштеги и эмодзи.</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>Локальный путь к картинке-обложке. Пусто — обложку не ставим.</summary>
        public string? CoverPath { get; set; }
    }

    public class TikTokVideo
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string UploadDate { get; set; } = string.Empty;
        public int Duration { get; set; }

        /// <summary>Unix timestamp публикации ролика.</summary>
        public long Timestamp { get; set; }

        /// <summary>
        /// Прямая ссылка на mp4 из embed-страницы. Нужна как запасной путь
        /// скачивания, если yt-dlp упрётся в очередную защиту TikTok.
        /// </summary>
        public string PlayAddr { get; set; } = string.Empty;

        /// <summary>
        /// Embed-страница не отдаёт длительность, поэтому её отсутствие ещё не
        /// значит, что это не видео (у yt-dlp длительность есть, и там проверка
        /// работает как раньше).
        /// </summary>
        public bool DurationUnknown { get; set; }

        public bool IsVideo => DurationUnknown || Duration > 0;
    }

    /// <summary>Запись в истории: какой ролик и когда он вышел.</summary>
    public class VideoHistoryEntry
    {
        public string Id { get; set; } = string.Empty;
        public long Timestamp { get; set; }
    }

    /// <summary>История по одной паре «Страница : TikTok-автор» — последние 5 роликов.</summary>
    public class TikTokAccountHistory
    {
        public List<VideoHistoryEntry> Videos { get; set; } = new List<VideoHistoryEntry>();

        public bool ContainsVideo(string videoId) => Videos.Any(v => v.Id == videoId);

        public long GetLatestTimestamp() => Videos.Count > 0 ? Videos.Max(v => v.Timestamp) : 0;

        public void AddVideo(string videoId, long timestamp)
        {
            Videos.Insert(0, new VideoHistoryEntry { Id = videoId, Timestamp = timestamp });

            if (Videos.Count > 5)
            {
                Videos = Videos.Take(5).ToList();
            }
        }
    }
}
