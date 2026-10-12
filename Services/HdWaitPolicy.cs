namespace FacebookReelsPublisher.Services
{
    /// <summary>
    /// Итог проверки качества: публиковать сейчас или подождать HD-версию.
    /// Message — строка для лога, по-русски.
    /// </summary>
    public sealed record HdCheck(bool Ready, string Message);

    /// <summary>
    /// Когда ролик готов к скачиванию, если ждать HD-версию.
    ///
    /// Почему ждать вообще. Свежий ролик TikTok хранит в единственном
    /// варианте — 576x1024 (h264, «normal_540»), со звуком 32 кбит/с. Так его
    /// видят все: yt-dlp, боты вроде snaptik (tikwm отдал тот же файл байт в
    /// байт), даже скачивание с водяным знаком. Лесенку HD (bytevc1 720p и
    /// 1080p, нередко 60 к/с, звук 64 кбит/с) TikTok достраивает позже.
    /// Замер 11–12.10.2026, 35 свежих роликов 9 авторов, опрос раз в 15 минут,
    /// 388 замеров: 720p и выше было у 54% роликов младше часа, у 73% — в
    /// 1–2 ч, у 80% — в 2–3 ч, у 90% — в 3–6 ч. 1080p появляется медленнее
    /// (23% / 27% / 30% в первые три окна) и не у всех: часть авторов сами
    /// заливают 720p, а ролики почти без просмотров могут остаться в 540p
    /// навсегда.
    ///
    /// Программа замечала ролик через 5–15 минут после выхода и почти всегда
    /// скачивала 576x1024. Такой файл Facebook помечал в статистике ролика:
    /// «Качество контента — нет компонента "высокое разрешение"». Плашку
    /// теперь снимает и растяжка до 1080x1920 (EnsureReelsResolutionAsync),
    /// но деталей растяжка не добавляет — их даёт только HD от самого TikTok.
    ///
    /// Ждём именно 1080p, а не «хоть 720p». VMAF против TikTok-овского 1080p
    /// (12.10.2026): у нарезок со стримов его 720p почти не лучше 540p —
    /// +2 пункта, битрейт там 250–410 кбит/с, — а 1080p даёт +14; у мемов 720p
    /// +16, 1080p +28. Все три случая, когда 1080p появился на глазах замера,
    /// уложились в первые 2 часа (0,4 / 0,7 / 1,8 ч). Кто сам заливает не выше
    /// 720p (markizomemes), тот прождёт срок впустую и уйдёт в 720p — цена
    /// правила, и платится она только задержкой.
    ///
    /// Правило:
    ///   • есть 1080p — качаем сразу;
    ///   • 1080p нет — ждём до конца срока, потом берём лучшее, что есть;
    ///   • узнать качество не вышло — тоже ждём и спрашиваем снова: не
    ///     ответил на вопрос — скорее всего, не отдаст сейчас и ролик, и
    ///     скачивание скатится на запасную ссылку 576x1024. Ролик при этом
    ///     не теряется: по сроку он уйдёт в любом случае.
    /// </summary>
    public static class HdWaitPolicy
    {
        /// <summary>Короткая сторона кадра у 1080p (1080x1920).</summary>
        public const int FullHdSide = 1080;

        /// <summary>Короткая сторона кадра у 720p (720x1280).</summary>
        public const int HdSide = 720;

        /// <param name="best">Лучший вариант, который TikTok отдаёт сейчас; null — узнать не вышло.</param>
        /// <param name="ageMinutes">Сколько минут назад ролик вышел.</param>
        /// <param name="waitMinutes">Сколько всего готовы ждать HD (WaitForHdMinutes).</param>
        /// <param name="nextCheckMinutes">Через сколько минут программа спросит снова — для лога.</param>
        public static HdCheck Decide(MediaInfo? best, double ageMinutes, int waitMinutes, int nextCheckMinutes)
        {
            if (waitMinutes <= 0)
            {
                return new HdCheck(true, string.Empty);
            }

            if (best == null)
            {
                return ageMinutes >= waitMinutes
                    ? new HdCheck(true, $"качество у TikTok узнать не удалось, а ждём уже {Minutes(ageMinutes)} — качаем как есть")
                    : new HdCheck(false,
                        $"Не удалось узнать у TikTok качество ролика — спросим снова через {Minutes(nextCheckMinutes)}. " +
                        $"Ролику {Minutes(ageMinutes)}, ждём не дольше {Minutes(waitMinutes)}");
            }

            var size = $"{best.Width}×{best.Height}";

            if (best.ShortSide >= FullHdSide)
            {
                return new HdCheck(true, $"TikTok отдаёт {size} — качаем в полном качестве");
            }

            if (ageMinutes >= waitMinutes)
            {
                return new HdCheck(true, $"1080p ждали {Minutes(waitMinutes)} — не дождались, берём лучшее, что есть: {size}");
            }

            if (best.ShortSide >= HdSide)
            {
                return new HdCheck(false,
                    $"Ждём 1080p: у TikTok пока есть {size}. Ролику {Minutes(ageMinutes)}, ждём до {Minutes(waitMinutes)}, " +
                    $"потом возьмём лучшее. Спросим снова через {Minutes(nextCheckMinutes)}");
            }

            return new HdCheck(false,
                $"Ждём HD: TikTok пока отдаёт этот ролик только в {size} — свежим роликам он делает HD позже, " +
                $"обычно за 1–2 часа. Ролику {Minutes(ageMinutes)}, ждём до {Minutes(waitMinutes)}. " +
                $"Спросим снова через {Minutes(nextCheckMinutes)}");
        }

        /// <summary>«45 мин», «1 ч 05 мин», «2 ч».</summary>
        public static string Minutes(double minutes)
        {
            var total = (int)Math.Max(0, Math.Round(minutes));
            if (total < 60) return $"{total} мин";

            var hours = total / 60;
            var rest = total % 60;
            return rest == 0 ? $"{hours} ч" : $"{hours} ч {rest:00} мин";
        }
    }
}
