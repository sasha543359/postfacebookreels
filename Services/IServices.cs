using FacebookReelsPublisher.Models;

namespace FacebookReelsPublisher.Services
{
    /// <summary>
    /// Публикация Reels на Страницу Facebook через Reels Publishing API.
    /// </summary>
    public interface IFacebookReelsService
    {
        /// <summary>Пройти весь путь: начать сеанс, залить файл, опубликовать и дождаться выхода ролика.</summary>
        Task<PublishResult> PublishReelAsync(VideoPublishInfo videoInfo);

        /// <summary>Статус видео: GET /{video_id}?fields=status.</summary>
        Task<ReelStatus> CheckStatusAsync(string videoId);

        /// <summary>
        /// Проверка доступа без единой публикации: живой ли токен, та ли Страница,
        /// есть ли право публиковать. Возвращает (получилось, что рассказал Facebook).
        /// </summary>
        Task<(bool Ok, string Details)> VerifyAccessAsync();
    }

    /// <summary>
    /// Мониторинг TikTok: что вышло нового и как это скачать.
    /// </summary>
    public interface ITikTokMonitorService
    {
        /// <summary>Последние ролики автора (до 5 штук).</summary>
        Task<List<TikTokVideo>> GetLatestVideos(string username);

        /// <summary>Есть ли у автора ролик, которого ещё не публиковали по этому ключу истории.</summary>
        Task<TikTokVideo?> CheckForNewVideo(string username, string historyKey);

        /// <summary>Скачать ролик.</summary>
        Task<string> DownloadVideo(TikTokVideo video, string? customPath = null);

        /// <summary>Отметить ролик как обработанный (сохранить ID и timestamp в историю).</summary>
        void MarkVideoAsProcessed(string historyKey, string videoId, long timestamp);
    }

    /// <summary>
    /// Уникализация видео перед публикацией: снятие watermark, ре-энкод,
    /// сигнатура под конкретную Страницу, звук, опциональная экранная подпись.
    /// </summary>
    public interface IVideoUniquifierService
    {
        /// <summary>
        /// Обрабатывает исходный файл под конкретную Страницу и возвращает путь к
        /// новому файлу. Если уникализатор выключен или что-то пошло не так —
        /// возвращает исходный путь, и публикация не ломается.
        /// </summary>
        Task<string> UniquifyAsync(string inputPath, string profile, string? onScreenCaption = null);

        /// <summary>
        /// Приводит файл к тем требованиям Reels, которые можно выполнить, не
        /// спрашивая: ролик длиннее 90 секунд подрезается до 90.
        ///
        /// Работает ВСЕГДА, даже когда уникализатор выключен. Иначе длинный
        /// ролик уходил бы в Facebook, получал отказ 1363128, не отмечался в
        /// истории — и возвращался на публикацию в каждом следующем цикле,
        /// вечно. Одна такая запись в списке авторов способна занять собой всю
        /// суточную квоту в 30 публикаций.
        /// </summary>
        Task<string> EnsureReelsDurationAsync(string path);

        /// <summary>Включён ли уникализатор.</summary>
        bool Enabled { get; }
    }

    /// <summary>
    /// Анти-спам троттлинг публикации: лимит за скользящие 24 часа + интервал на Страницу.
    /// </summary>
    public interface IPublishThrottle
    {
        /// <summary>Можно ли сейчас постить с этой Страницы; reason — причина отказа для лога.</summary>
        bool CanPost(string page, out string reason);

        /// <summary>Зафиксировать успешную публикацию.</summary>
        void RecordPost(string page);

        /// <summary>Сколько публикаций эта Страница сделала за последние 24 часа.</summary>
        int PostsLast24h(string page);
    }
}
