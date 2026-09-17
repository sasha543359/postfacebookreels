using FacebookReelsPublisher.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace FacebookReelsPublisher.Services
{
    /// <summary>
    /// Анти-спам: лимит публикаций на Страницу за скользящие 24 часа плюс
    /// минимальный интервал между публикациями.
    ///
    /// ПОЧЕМУ ОКНО СКОЛЬЗЯЩЕЕ, А НЕ «СУТКИ UTC»
    /// ─────────────────────────────────────────
    /// Facebook считает именно так: «не более 30 публикаций через API в течение
    /// скользящего 24-часового периода». Календарный счётчик обнуляется в
    /// полночь, и тогда 30 роликов в 23:50 плюс 30 в 00:10 — по нашим меркам два
    /// разных дня, а по меркам Facebook одно окно. Вторая пачка начала бы
    /// отбиваться ошибкой 613, и по логам это выглядело бы как поломка
    /// публикации, хотя сломан был бы счётчик.
    ///
    /// ПОЧЕМУ ХРАНИМ ВРЕМЕНА, А НЕ ЧИСЛО
    /// ──────────────────────────────────
    /// Из списка времён считается и лимит за сутки, и интервал между постами.
    /// Интервал поэтому переживает перезапуск контейнера: если держать время
    /// последнего поста только в памяти, после каждого рестарта пауза забывалась
    /// бы и Страница могла выложить два ролика подряд.
    /// </summary>
    public class PublishThrottleService : IPublishThrottle
    {
        private readonly PublishingSettings _cfg;
        private readonly ILogger<PublishThrottleService> _logger;
        private readonly object _lock = new();

        /// <summary>{ "имя Страницы": [unix-время публикации, ...] }</summary>
        private Dictionary<string, List<long>> _posts = new();

        public PublishThrottleService(IOptions<PublishingSettings> cfg, ILogger<PublishThrottleService> logger)
        {
            _cfg = cfg.Value;
            _logger = logger;
            Load();
        }

        private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private const long Day = 24 * 60 * 60;

        public bool CanPost(string page, out string reason)
        {
            reason = string.Empty;

            lock (_lock)
            {
                var times = Recent(page);

                // Потолок самого Facebook. Действует всегда, даже если в
                // настройках написали больше или сняли лимит нулём: перешагнуть
                // его всё равно не выйдет, API просто начнёт отвечать 613.
                var hard = _cfg.ApiHardLimitPer24h > 0 ? _cfg.ApiHardLimitPer24h : int.MaxValue;

                // Наш собственный лимит. 0 означает «без лимита», но потолок
                // Facebook при этом остаётся.
                var soft = _cfg.MaxPostsPerPagePerDay > 0 ? _cfg.MaxPostsPerPagePerDay : int.MaxValue;

                var limit = Math.Min(hard, soft);

                if (times.Count >= limit)
                {
                    var oldest = times.Min();
                    var freeIn = TimeSpan.FromSeconds(Math.Max(0, oldest + Day - Now));

                    reason = limit == hard && soft > hard
                        ? $"достигнут потолок Facebook: {hard} публикаций за 24 часа " +
                          $"(освободится через {freeIn.Hours}ч {freeIn.Minutes}м)"
                        : $"лимит {limit} публикаций за 24 часа достигнут ({times.Count}), " +
                          $"освободится через {freeIn.Hours}ч {freeIn.Minutes}м";

                    return false;
                }

                if (_cfg.MinMinutesBetweenPosts > 0 && times.Count > 0)
                {
                    var minutes = (Now - times.Max()) / 60.0;

                    if (minutes < _cfg.MinMinutesBetweenPosts)
                    {
                        reason = $"интервал: прошло {minutes:F0}м из {_cfg.MinMinutesBetweenPosts}м";
                        return false;
                    }
                }

                return true;
            }
        }

        public void RecordPost(string page)
        {
            lock (_lock)
            {
                var times = Recent(page);
                times.Add(Now);
                _posts[page] = times;
                Save();
            }
        }

        public int PostsLast24h(string page)
        {
            lock (_lock)
            {
                return Recent(page).Count;
            }
        }

        /// <summary>
        /// Публикации этой Страницы за последние 24 часа. Заодно выбрасывает
        /// протухшие записи, чтобы файл не рос бесконечно.
        /// Вызывать только под _lock.
        /// </summary>
        private List<long> Recent(string page)
        {
            var edge = Now - Day;

            if (!_posts.TryGetValue(page, out var times) || times == null)
            {
                return new List<long>();
            }

            var fresh = times.Where(t => t > edge).OrderBy(t => t).ToList();

            if (fresh.Count != times.Count)
            {
                _posts[page] = fresh;
            }

            return fresh;
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_cfg.CountsPath))
                {
                    _posts = new Dictionary<string, List<long>>();
                    return;
                }

                var json = File.ReadAllText(_cfg.CountsPath);
                _posts = JsonConvert.DeserializeObject<Dictionary<string, List<long>>>(json)
                         ?? new Dictionary<string, List<long>>();
            }
            catch (Exception ex)
            {
                // Файл мог остаться от прошлой версии, где лежал другой формат
                // ({ "дата": { "аккаунт": число } }). Разобрать его нечем, но и
                // падать не из-за чего: худшее последствие — одна Страница
                // сможет выложить лишний ролик в первые сутки после обновления.
                _logger.LogWarning(
                    $"Счётчик публикаций не прочитался ({ex.Message}) — начинаем с чистого. " +
                    "Если файл остался от прошлой версии программы, это нормально и бывает один раз.");
                _posts = new Dictionary<string, List<long>>();
            }
        }

        private void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(_cfg.CountsPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(_cfg.CountsPath, JsonConvert.SerializeObject(_posts, Formatting.Indented));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось сохранить счётчик публикаций.");
            }
        }
    }
}
