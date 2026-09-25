using FacebookReelsPublisher.Models;
using FacebookReelsPublisher.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace FacebookReelsPublisher
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // Без этого на Windows кириллица в консоли превращается в кашу:
            // консоль по умолчанию сидит на кодовой странице 866, а программа
            // пишет UTF-8. В Docker всё и так UTF-8, но запускают её и с
            // рабочего стола.
            try
            {
                Console.OutputEncoding = System.Text.Encoding.UTF8;
            }
            catch
            {
                // Перенаправленный вывод кодировку менять не даёт — не беда.
            }

            AnsiConsole.Write(
                new FigletText("TikTok -> FB Reels")
                    .Centered()
                    .Color(Color.Blue));

            AnsiConsole.MarkupLine("[grey]Автоматическая публикация видео из TikTok в Facebook Reels[/]\n");

            var services = ConfigureServices();
            var serviceProvider = services.BuildServiceProvider();

            var tiktokMonitor = serviceProvider.GetRequiredService<ITikTokMonitorService>();
            var uniquifier = serviceProvider.GetRequiredService<IVideoUniquifierService>();
            var throttle = serviceProvider.GetRequiredService<IPublishThrottle>();
            var config = serviceProvider.GetRequiredService<IConfiguration>();

            var appSettings = new AppSettings();
            config.Bind(appSettings);

            var pages = appSettings.FacebookPages;

            // Список авторов правят руками, и до монитора он должен доехать в том
            // виде, который понимает TikTok. Иначе кривая запись выглядит в логах
            // как «аккаунта нет» — то есть как чужая поломка.
            NormalizeTikTokUsernames(pages);

            // Имя Страницы — ключ её истории и суточного счётчика. Две рабочие
            // Страницы с одним именем (обычная ошибка при копировании блока в
            // appsettings.json) делили бы лимит на двоих, а общего автора
            // публиковала бы только первая — и ничто в логе на это не указало бы.
            // Проверка стоит до --check, чтобы он первым о ней и сказал.
            var duplicate = pages
                .Where(p => p.NotReadyReason == null)
                .GroupBy(p => p.PageName.Trim(), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1);
            if (duplicate != null)
            {
                AnsiConsole.MarkupLine($"[red]❌ В appsettings.json у двух Страниц одинаковое имя PageName: «{Esc(duplicate.Key)}».[/]");
                AnsiConsole.MarkupLine("[yellow]   У каждой Страницы должно быть своё имя — по нему программа помнит, что уже выложила.\n" +
                                       "   Поменяй его в скопированном блоке и пересобери: docker compose up -d --build[/]");
                Environment.ExitCode = 1;
                return;
            }

            var server = new ServerSettings();
            config.GetSection("Server").Bind(server);

            // Проверка без единой публикации: живы ли токены Страниц и читаются
            // ли ленты авторов. Нужна потому, что по рабочему логу нельзя понять,
            // что именно чинить: мёртвый ник, просроченный токен и временная
            // блокировка выглядят там одинаково, а цикл идёт часами.
            if (args.Any(a => a is "--check" or "-c"))
            {
                await RunCheckAsync(
                    serviceProvider,
                    tiktokMonitor,
                    pages,
                    appSettings,
                    server,
                    args.Where(a => !a.StartsWith('-')).ToArray(),
                    download: args.Any(a => a == "--download"));
                return;
            }

            // Свой файл тем же конвейером, что и ролик из TikTok. Нужен, чтобы
            // проверить Страницу сразу, а не ждать, пока кто-то из авторов
            // выложит новое видео.
            var fileArg = Array.IndexOf(args, "--publish-file");
            if (fileArg >= 0)
            {
                await RunPublishFileAsync(
                    serviceProvider,
                    config,
                    appSettings,
                    server,
                    pages,
                    tiktokMonitor,
                    uniquifier,
                    throttle,
                    file: fileArg + 1 < args.Length ? args[fileArg + 1] : null,
                    pageName: fileArg + 2 < args.Length ? args[fileArg + 2] : null);
                return;
            }

            var checkInterval = appSettings.CheckIntervalMinutes;
            var tiktokDelay = appSettings.TikTokCheckDelaySeconds;

            if (pages.Count == 0)
            {
                AnsiConsole.MarkupLine("[red]❌ ОШИБКА: Страницы Facebook не настроены![/]");
                AnsiConsole.MarkupLine("[yellow]Откройте appsettings.json и заполните FacebookPages[/]");
                return;
            }

            DisplaySettings(pages, appSettings, server);

            var cycleCount = 0;

            while (true)
            {
                try
                {
                    cycleCount++;

                    AnsiConsole.Write(new Rule($"[cyan]Цикл #{cycleCount} - {DateTime.Now:HH:mm:ss}[/]"));
                    AnsiConsole.WriteLine();

                    var maxTikTokAccounts = pages.Max(p => p.TikTokUsernames.Count);

                    if (maxTikTokAccounts == 0)
                    {
                        AnsiConsole.MarkupLine("[yellow]⚠️  Ни у одной Страницы нет TikTok-авторов. Спим до следующего цикла.[/]\n");
                        await Task.Delay(TimeSpan.FromMinutes(checkInterval));
                        continue;
                    }

                    AnsiConsole.MarkupLine($"[grey]🔄 Алгоритм: проходим по {maxTikTokAccounts} позициям в массивах[/]\n");

                    // АЛГОРИТМ «КАРУСЕЛЬ»: берём первого автора у каждой Страницы,
                    // потом второго, и так далее. Так ни одна Страница не съедает
                    // весь цикл, если у неё длинный список авторов.
                    for (var tiktokIndex = 0; tiktokIndex < maxTikTokAccounts; tiktokIndex++)
                    {
                        AnsiConsole.MarkupLine($"[yellow]═══ Позиция #{tiktokIndex + 1} в массивах TikTok ═══[/]\n");

                        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
                        {
                            var page = pages[pageIndex];

                            if (page.NotReadyReason is { } notReady)
                            {
                                AnsiConsole.MarkupLine($"[grey]⏭️  {Esc(page.PageName)} - {Esc(notReady)}, пропускаем[/]\n");
                                continue;
                            }

                            if (tiktokIndex >= page.TikTokUsernames.Count)
                            {
                                AnsiConsole.MarkupLine($"[grey]⏭️  {Esc(page.PageName)} - нет TikTok на позиции {tiktokIndex + 1}, пропускаем[/]");
                                continue;
                            }

                            var tiktokUsername = page.TikTokUsernames[tiktokIndex];
                            var historyKey = $"{page.PageName}:{tiktokUsername}";

                            AnsiConsole.MarkupLine($"[blue]📍 Страница: {Esc(page.PageName)} → TikTok: @{Esc(tiktokUsername)}[/]");

                            // АНТИ-СПАМ: если Страница уже выбрала лимит за сутки
                            // или ещё не прошёл минимальный интервал — не трогаем
                            // её вовсе, чтобы не дёргать TikTok впустую.
                            if (!throttle.CanPost(page.PageName, out var throttleReason))
                            {
                                AnsiConsole.MarkupLine($"[grey]   ⏸️  {Esc(throttleReason)} — пропускаем {Esc(page.PageName)}[/]\n");
                                continue;
                            }

                            try
                            {
                                var newVideo = await tiktokMonitor.CheckForNewVideo(tiktokUsername, historyKey);

                                if (newVideo == null)
                                {
                                    AnsiConsole.MarkupLine("[grey]   Нет новых видео[/]\n");
                                    await DelayBetweenChecks(pageIndex, pages.Count, tiktokIndex, maxTikTokAccounts, tiktokDelay);
                                    continue;
                                }

                                AnsiConsole.MarkupLine($"[green]🎉 Найдено новое видео на @{Esc(tiktokUsername)}![/]");
                                AnsiConsole.MarkupLine($"[grey]   Название:[/] {Esc(newVideo.Title)}");
                                AnsiConsole.MarkupLine($"[grey]   ID:[/] {Esc(newVideo.Id)}");
                                AnsiConsole.MarkupLine($"[grey]   Timestamp:[/] {newVideo.Timestamp}\n");

                                var facebookService = CreateFacebookService(serviceProvider, config, page, server);

                                await DownloadAndPublishVideo(
                                    newVideo,
                                    tiktokUsername,
                                    historyKey,
                                    page,
                                    appSettings,
                                    server,
                                    tiktokMonitor,
                                    facebookService,
                                    uniquifier,
                                    throttle);
                            }
                            catch (Exception ex)
                            {
                                AnsiConsole.MarkupLine($"[red]❌ Ошибка обработки @{Esc(tiktokUsername)}: {Esc(ex.Message)}[/]");
                                AnsiConsole.MarkupLine("[yellow]   Продолжаем проверку других аккаунтов...[/]\n");
                            }

                            await DelayBetweenChecks(pageIndex, pages.Count, tiktokIndex, maxTikTokAccounts, tiktokDelay);
                        }

                        AnsiConsole.WriteLine();
                    }

                    AnsiConsole.MarkupLine($"[grey]💤 Цикл завершён. Следующая проверка через {checkInterval} минут...[/]\n");
                    await Task.Delay(TimeSpan.FromMinutes(checkInterval));
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine("[red]❌ Критическая ошибка в цикле:[/]");
                    AnsiConsole.WriteException(ex);
                    AnsiConsole.MarkupLine("\n[grey]⏱️  Повторная попытка через 1 минуту...[/]\n");
                    await Task.Delay(TimeSpan.FromMinutes(1));
                }
            }
        }

        static async Task DelayBetweenChecks(int pageIndex, int pageCount, int tiktokIndex, int maxTikTok, int seconds)
        {
            // Пауза не для вежливости к TikTok, а против капчи: без неё на
            // длинном списке страница начинает подменяться проверкой.
            var isLast = pageIndex >= pageCount - 1 && tiktokIndex >= maxTikTok - 1;
            if (isLast) return;

            AnsiConsole.MarkupLine($"[grey]   ⏳ Задержка {seconds}с перед следующей проверкой...[/]");
            await Task.Delay(TimeSpan.FromSeconds(seconds));
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Режим --check
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Проходит по Страницам и авторам и говорит, что с каждым из них, не
        /// публикуя ничего. Сначала Facebook (токен живой? та ли Страница?),
        /// потом TikTok. Порядок такой, потому что мёртвый токен обесценивает
        /// любую проверку лент: роликов может найтись сколько угодно, а выложить
        /// их будет нечем.
        /// </summary>
        static async Task RunCheckAsync(
            IServiceProvider serviceProvider,
            ITikTokMonitorService monitor,
            List<FacebookPageSettings> pages,
            AppSettings appSettings,
            ServerSettings server,
            string[] names,
            bool download)
        {
            var config = serviceProvider.GetRequiredService<IConfiguration>();

            // ── Facebook ─────────────────────────────────────────────────────
            AnsiConsole.MarkupLine("\n[cyan]Проверяем Страницы Facebook[/]\n");

            var fbTable = new Table().Border(TableBorder.Rounded);
            fbTable.AddColumn("Страница");
            fbTable.AddColumn("PageId");
            fbTable.AddColumn("Итог");

            foreach (var page in pages)
            {
                if (page.NotReadyReason is { } notReady)
                {
                    fbTable.AddRow(Esc(page.PageName), Esc(page.PageId), $"[grey]{Esc(notReady)} — Страница выключена[/]");
                    continue;
                }

                var service = CreateFacebookService(serviceProvider, config, page, server);
                var (ok, details) = await service.VerifyAccessAsync();

                fbTable.AddRow(
                    Esc(page.PageName),
                    Esc(page.PageId),
                    ok ? $"[green]{Esc(details)}[/]" : $"[red]{Esc(details)}[/]");
            }

            AnsiConsole.Write(fbTable);

            // ── Описание ─────────────────────────────────────────────────────
            // Готовое описание каждой Страницы: правку CustomCaption, подписи
            // или хэштегов видно до первой публикации, а не после неё.
            foreach (var page in pages.Where(p => p.NotReadyReason == null))
            {
                var sample = new TikTokVideo { Title = "‹текст ролика из TikTok›" };
                var description = BuildDescription(
                    sample,
                    page.CustomCaption,
                    appSettings.RequiredCaptionSuffix,
                    HashtagsFor(page, appSettings));

                AnsiConsole.MarkupLine($"\n[cyan]Описание под роликами {Esc(page.PageName)}:[/]");
                foreach (var line in description.Split('\n'))
                {
                    AnsiConsole.MarkupLine($"[grey]   │[/] {Esc(line)}");
                }

                if (string.IsNullOrWhiteSpace(page.CustomCaption))
                {
                    AnsiConsole.MarkupLine("[grey]   (хэштеги дописываются, только если в тексте ролика своих нет)[/]");
                }
            }

            // ── TikTok ───────────────────────────────────────────────────────
            // Авторов выключенных Страниц не проверяем: в шаблоне у второй
            // Страницы стоят ники-примеры, и заполнивший только первую видел бы
            // в таблице чужие ники. Не готова ни одна Страница — проверяем
            // всех, чтобы ленты можно было проверить ещё до токенов.
            var ready = pages.Where(p => p.NotReadyReason == null).ToList();
            var skipped = ready.Count > 0 && names.Length == 0
                ? pages.Where(p => p.NotReadyReason != null && p.TikTokUsernames.Count > 0).ToList()
                : new List<FacebookPageSettings>();

            var targets = (names.Length > 0
                    ? names.Select(TikTokMonitorService.NormalizeUsername)
                    : (ready.Count > 0 ? ready : pages).SelectMany(p => p.TikTokUsernames))
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (skipped.Count > 0)
            {
                AnsiConsole.MarkupLine($"\n[grey]Авторов выключенных Страниц не проверяем: {Esc(string.Join(", ", skipped.Select(p => p.PageName)))}[/]");
            }

            if (targets.Count == 0)
            {
                AnsiConsole.MarkupLine("\n[yellow]TikTok-авторов в настройках нет — проверять нечего[/]");
                return;
            }

            AnsiConsole.MarkupLine($"\n[cyan]Проверяем {targets.Count} автор(ов) TikTok, ничего не публикуя[/]\n");

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("TikTok");
            table.AddColumn("Роликов");
            table.AddColumn("Последний");
            table.AddColumn("Когда (UTC)");
            table.AddColumn("Итог");

            for (var i = 0; i < targets.Count; i++)
            {
                var username = targets[i];

                AnsiConsole.MarkupLine($"[grey]— @{Esc(username)}[/]");

                var videos = await monitor.GetLatestVideos(username);
                var newest = videos.FirstOrDefault();

                var verdict = newest == null
                    ? "[red]роликов нет[/]"
                    : "[green]лента читается[/]";

                if (newest != null && download)
                {
                    try
                    {
                        var path = await monitor.DownloadVideo(newest);
                        var mb = new FileInfo(path).Length / 1024.0 / 1024.0;
                        verdict = $"[green]скачано {mb:F2} МБ[/]";
                    }
                    catch (Exception ex)
                    {
                        verdict = $"[red]не скачалось: {Esc(ex.Message.Split('\n')[0])}[/]";
                    }
                }

                table.AddRow(
                    $"@{Esc(username)}",
                    videos.Count.ToString(),
                    Esc(newest?.Id ?? "—"),
                    newest == null
                        ? "—"
                        : DateTimeOffset.FromUnixTimeSeconds(newest.Timestamp).UtcDateTime.ToString("dd.MM HH:mm"),
                    verdict);

                if (i < targets.Count - 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
            }

            AnsiConsole.WriteLine();
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine(
                "\n[grey]«роликов нет» рядом с живым аккаунтом означает капчу или блокировку адреса;" +
                " если выше стоит ⛔ — такого ника у TikTok нет, и правится это только в appsettings.json[/]");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Режим --publish-file
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Публикует свой файл так, будто он пришёл из TikTok: уникализатор,
        /// подрезка, описание с обязательной подписью, лимиты Страницы. Файл
        /// должен лежать внутри контейнера — на сервере это папка
        /// /var/www/videos, она общая с хостом.
        /// </summary>
        static async Task RunPublishFileAsync(
            IServiceProvider serviceProvider,
            IConfiguration config,
            AppSettings appSettings,
            ServerSettings server,
            List<FacebookPageSettings> pages,
            ITikTokMonitorService monitor,
            IVideoUniquifierService uniquifier,
            IPublishThrottle throttle,
            string? file,
            string? pageName)
        {
            if (file == null || !File.Exists(file))
            {
                AnsiConsole.MarkupLine($"[red]❌ Файл не найден: {Esc(file ?? "не указан")}[/]");
                AnsiConsole.MarkupLine("[grey]   Пример: docker compose run --rm app --publish-file /var/www/videos/ролик.mp4 [[ИмяСтраницы]][/]");
                Environment.ExitCode = 1;
                return;
            }

            var page = pageName == null
                ? pages.FirstOrDefault(p => p.NotReadyReason == null)
                : pages.FirstOrDefault(p => string.Equals(p.PageName, pageName, StringComparison.OrdinalIgnoreCase));

            if (page == null)
            {
                AnsiConsole.MarkupLine(pageName == null
                    ? "[red]❌ Ни у одной Страницы в appsettings.json не вставлены PageId и PageAccessToken[/]"
                    : $"[red]❌ Страницы {Esc(pageName)} нет в appsettings.json (смотри поле PageName)[/]");
                Environment.ExitCode = 1;
                return;
            }

            if (page.NotReadyReason is { } notReady)
            {
                AnsiConsole.MarkupLine($"[red]❌ У Страницы {Esc(page.PageName)} {Esc(notReady)} в appsettings.json[/]");
                Environment.ExitCode = 1;
                return;
            }

            // Лимиты те же, что у цикла: ручная публикация тоже идёт в зачёт
            // 30 роликов за 24 часа, которые Facebook считает сам.
            if (!throttle.CanPost(page.PageName, out var reason))
            {
                AnsiConsole.MarkupLine($"[yellow]⏸️  {Esc(reason)} — публикацию не начинаем[/]");
                Environment.ExitCode = 1;
                return;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var video = new TikTokVideo { Id = $"file-{now}", Timestamp = now };

            AnsiConsole.MarkupLine($"[blue]📍 Страница: {Esc(page.PageName)} ← файл {Esc(Path.GetFileName(file))}[/]\n");

            var published = false;

            try
            {
                published = await DownloadAndPublishVideo(
                    video,
                    Path.GetFileName(file),
                    historyKey: null,
                    page,
                    appSettings,
                    server,
                    monitor,
                    CreateFacebookService(serviceProvider, config, page, server),
                    uniquifier,
                    throttle,
                    preparedFile: file);
            }
            catch
            {
                // Текст ошибки уже выведен внутри конвейера.
            }

            // Для тех, кто запускает команду из скрипта: неудача видна по коду выхода.
            if (!published)
            {
                Environment.ExitCode = 1;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Нормализация ников
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Приводит списки TikTok-ников к виду, который понимает монитор, и
        /// убирает повторы.
        ///
        /// Зачем чистка: в конфиг попадают ссылки на профиль, «@ник», ник с
        /// пробелом по краям и записи вида «ник:7562467187447120952». Все они
        /// ломают адрес embed-страницы.
        ///
        /// Зачем дедупликация: обход идёт по позициям в массивах, поэтому один и
        /// тот же автор, указанный дважды, занимает два круга и вытесняет
        /// остальных, а публиковаться с него всё равно будет только первое видео.
        /// </summary>
        static void NormalizeTikTokUsernames(List<FacebookPageSettings> pages)
        {
            foreach (var page in pages)
            {
                var cleaned = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var raw in page.TikTokUsernames)
                {
                    var shown = Esc(raw ?? string.Empty);
                    var name = TikTokMonitorService.NormalizeUsername(raw);

                    if (name.Length == 0)
                    {
                        AnsiConsole.MarkupLine(
                            $"[yellow]⚠️  {Esc(page.PageName)}: запись «{shown}» — не ник TikTok, пропускаем[/]");
                        continue;
                    }

                    if (!seen.Add(name))
                    {
                        AnsiConsole.MarkupLine(
                            $"[yellow]⚠️  {Esc(page.PageName)}: @{Esc(name)} указан дважды — оставляем один[/]");
                        continue;
                    }

                    if (!string.Equals(name, raw, StringComparison.Ordinal))
                    {
                        AnsiConsole.MarkupLine($"[grey]   ✎ {Esc(page.PageName)}: «{shown}» → @{Esc(name)}[/]");
                    }

                    cleaned.Add(name);
                }

                page.TikTokUsernames = cleaned;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Скачать и опубликовать
        // ─────────────────────────────────────────────────────────────────────

        /// <param name="historyKey">null — ролик не из TikTok (режим --publish-file), в историю авторов его не пишем.</param>
        /// <param name="preparedFile">Готовый файл вместо скачивания. Работаем с его копией: конвейер удаляет свои файлы по ходу дела.</param>
        static async Task<bool> DownloadAndPublishVideo(
            TikTokVideo newVideo,
            string tiktokUsername,
            string? historyKey,
            FacebookPageSettings page,
            AppSettings appSettings,
            ServerSettings server,
            ITikTokMonitorService tiktokMonitor,
            IFacebookReelsService facebookService,
            IVideoUniquifierService uniquifier,
            IPublishThrottle throttle,
            string? preparedFile = null)
        {
            var published = false;

            // Сообщения при неудаче: у цикла ролик вернётся сам, а ручную
            // публикацию повторяет человек.
            var retryHint = historyKey != null
                ? "ID НЕ сохранён — попробуем снова в следующем цикле"
                : "Ролик не опубликован — запусти команду ещё раз";

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("green bold"))
                .StartAsync("Скачиваем и публикуем видео...", async ctx =>
                {
                    string? localPath = null;

                    try
                    {
                        if (preparedFile != null)
                        {
                            localPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(preparedFile))!, $"{newVideo.Id}.mp4");
                            File.Copy(preparedFile, localPath, overwrite: true);
                        }
                        else
                        {
                            ctx.Status("📥 Скачиваем видео с TikTok...");
                            localPath = await tiktokMonitor.DownloadVideo(newVideo);
                        }

                        // УНИКАЛИЗАЦИЯ (если включена). При любом сбое сервис
                        // возвращает исходник — публикация не ломается.
                        if (uniquifier.Enabled)
                        {
                            ctx.Status("🎛️ Уникализируем видео...");

                            var profile = string.IsNullOrWhiteSpace(page.UniquifierProfile)
                                ? page.PageName
                                : page.UniquifierProfile;

                            var originalPath = localPath;
                            localPath = await uniquifier.UniquifyAsync(originalPath, profile, page.OnScreenCaption);

                            if (!string.Equals(localPath, originalPath, StringComparison.Ordinal) && File.Exists(originalPath))
                            {
                                try { File.Delete(originalPath); } catch { }
                            }
                        }

                        // Длительность проверяем ВСЕГДА, даже с выключенным
                        // уникализатором: ролик длиннее 90 секунд Facebook не
                        // примет никогда, а без подрезки он возвращался бы на
                        // публикацию в каждом цикле.
                        ctx.Status("✂️ Проверяем длительность...");
                        localPath = await uniquifier.EnsureReelsDurationAsync(localPath);

                        var fileSize = new FileInfo(localPath).Length / 1024.0 / 1024.0;
                        AnsiConsole.MarkupLine($"[green]✓[/] Видео готово: [grey]{Esc(Path.GetFileName(localPath))}[/]");
                        AnsiConsole.MarkupLine($"[grey]   Размер:[/] {fileSize:F2} МБ");

                        // Публичная ссылка нужна только режимам "url" и "auto".
                        // В режиме "bytes" её может не быть вовсе — тогда файл
                        // уедет телом запроса, и белый IP не понадобится.
                        string? publicUrl = null;
                        if (!string.IsNullOrWhiteSpace(server.PublicUrl))
                        {
                            publicUrl = $"{server.PublicUrl.TrimEnd('/')}/videos/{Path.GetFileName(localPath)}";
                            AnsiConsole.MarkupLine($"[green]✓[/] Ссылка для Facebook: [grey]{Esc(publicUrl)}[/]");
                        }

                        // Обложка: у Facebook это локальный ФАЙЛ, а не ссылка.
                        string? coverPath = null;
                        if (!string.IsNullOrWhiteSpace(page.CoverImage))
                        {
                            coverPath = Path.Combine(server.CoverPath, page.CoverImage);
                            AnsiConsole.MarkupLine($"[grey]   🖼️  Обложка:[/] {Esc(coverPath)}");
                        }

                        // Esc обязателен: статус рисуется в фоновом потоке, и
                        // скобка в имени Страницы роняла там весь процесс мимо
                        // любого catch.
                        ctx.Status($"📘 Публикуем Reels ({Esc(page.PageName)})...");

                        var description = BuildDescription(
                            newVideo,
                            page.CustomCaption,
                            appSettings.RequiredCaptionSuffix,
                            HashtagsFor(page, appSettings));

                        // Показываем итоговое описание целиком. Оно собирается из
                        // трёх кусков (текст, обязательная подпись, хэштеги), и без
                        // этой строчки проверить, что подпись на месте, можно было бы
                        // только открыв опубликованный ролик.
                        AnsiConsole.MarkupLine("[grey]   📝 Описание:[/]");
                        foreach (var line in description.Split('\n'))
                        {
                            AnsiConsole.MarkupLine($"[grey]      │[/] {Esc(line)}");
                        }

                        var result = await facebookService.PublishReelAsync(new VideoPublishInfo
                        {
                            FilePath = localPath,
                            VideoUrl = publicUrl,
                            Description = description,
                            CoverPath = coverPath
                        });

                        AnsiConsole.WriteLine();

                        if (result.Success)
                        {
                            published = true;

                            if (historyKey != null)
                            {
                                tiktokMonitor.MarkVideoAsProcessed(historyKey, newVideo.Id, newVideo.Timestamp);
                            }
                            throttle.RecordPost(page.PageName);

                            var source = historyKey != null
                                ? $"[grey]TikTok:[/] [cyan]@{Esc(tiktokUsername)}[/]\n"
                                : $"[grey]Файл:[/] [cyan]{Esc(tiktokUsername)}[/]\n";

                            var successPanel = new Panel(
                                new Markup($"[green]✓ Reels успешно опубликован![/]\n\n" +
                                           $"[grey]Страница:[/] [cyan]{Esc(page.PageName)}[/]\n" +
                                           $"[grey]Video ID:[/] [yellow]{Esc(result.VideoId ?? "—")}[/]\n" +
                                           $"[grey]Post ID:[/] [yellow]{Esc(result.PostId ?? "—")}[/]\n" +
                                           source +
                                           $"[grey]За 24 часа:[/] [cyan]{throttle.PostsLast24h(page.PageName)}[/]"))
                            {
                                Border = BoxBorder.Double,
                                BorderStyle = new Style(Color.Green)
                            };
                            AnsiConsole.Write(successPanel);
                        }
                        else if (result.IsPermanentlyRejected)
                        {
                            // Такой файл Facebook не примет ни с какой попытки.
                            // Записываем ролик в историю, чтобы он не приходил
                            // сюда снова каждые несколько минут: иначе один
                            // негодный ролик способен занять собой весь цикл и
                            // всю суточную квоту.
                            if (historyKey != null)
                            {
                                tiktokMonitor.MarkVideoAsProcessed(historyKey, newVideo.Id, newVideo.Timestamp);
                            }

                            var skipPanel = new Panel(
                                new Markup($"[yellow]⏭️  Facebook не примет этот ролик[/]\n\n" +
                                           $"[grey]{Esc(result.ErrorMessage ?? "без текста")}[/]\n\n" +
                                           $"[grey]Дело в самом файле, а не в связи, поэтому повторять нечего.[/]\n" +
                                           (historyKey != null
                                               ? "[grey]Ролик помечен как обработанный и больше не будет пробоваться.[/]"
                                               : "[grey]С этим файлом повторять команду бесполезно — нужен другой ролик.[/]")))
                            {
                                Border = BoxBorder.Double,
                                BorderStyle = new Style(Color.Yellow)
                            };
                            AnsiConsole.Write(skipPanel);
                        }
                        else
                        {
                            var errorPanel = new Panel(
                                new Markup($"[red]❌ Ошибка публикации[/]\n\n" +
                                           $"[grey]{Esc(result.ErrorMessage ?? "без текста")}[/]\n\n" +
                                           $"[yellow]{retryHint}[/]"))
                            {
                                Border = BoxBorder.Double,
                                BorderStyle = new Style(Color.Red)
                            };
                            AnsiConsole.Write(errorPanel);
                        }

                        DeleteLocal(localPath);
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.WriteLine();
                        AnsiConsole.MarkupLine($"[red]❌ Ошибка: {Esc(ex.Message)}[/]");
                        AnsiConsole.MarkupLine($"[yellow]{retryHint}[/]");

                        DeleteLocal(localPath);
                        throw;
                    }
                });

            return published;
        }

        static void DeleteLocal(string? path)
        {
            if (path == null || !File.Exists(path)) return;

            try
            {
                File.Delete(path);
                AnsiConsole.MarkupLine($"\n[grey]🗑️  Удалён локальный файл: {Esc(Path.GetFileName(path))}[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]⚠️  Не удалось удалить файл: {Esc(ex.Message)}[/]");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Описание под Reels
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Собирает описание: обязательная подпись, текст, хэштеги.
        ///
        /// Обязательная подпись (RequiredCaptionSuffix, по умолчанию
        /// «Twitch: MELLSTROY») добавляется ВСЕГДА — и к описанию исходного
        /// ролика, и к CustomCaption — и идёт ПЕРВОЙ строкой: Facebook под
        /// роликом показывает только начало описания, остальное прячет за
        /// «…ещё», а реклама канала должна быть видна сразу. Так попросил
        /// владелец 25.09.2026 («описание после твича, потом теги»); раньше
        /// строка шла в конце — отсюда «Suffix» в названии настройки.
        /// Если строка уже есть в тексте, второй раз не добавляется.
        /// </summary>
        static string BuildDescription(TikTokVideo video, string? customCaption, string? requiredSuffix, string? defaultHashtags)
        {
            var text = (!string.IsNullOrWhiteSpace(customCaption) ? customCaption : video.Title ?? string.Empty).Trim();

            // Описание TikTok иногда бывает огромным. Режем его, а не готовое
            // описание целиком: обрезать надо исходный текст, чтобы обязательная
            // подпись гарантированно осталась на месте.
            if (text.Length > MaxSourceTextLength)
            {
                text = text[..MaxSourceTextLength].TrimEnd() + "…";
            }

            var parts = new List<string>();

            var suffix = (requiredSuffix ?? string.Empty).Trim();
            if (suffix.Length > 0 && text.IndexOf(suffix, StringComparison.OrdinalIgnoreCase) < 0)
            {
                parts.Add(suffix);
            }

            if (text.Length > 0)
            {
                parts.Add(text);
            }

            var hashtags = (defaultHashtags ?? string.Empty).Trim();
            if (hashtags.Length > 0 && !text.Contains('#'))
            {
                parts.Add(hashtags);
            }

            return string.Join("\n\n", parts);
        }

        private const int MaxSourceTextLength = 1500;

        /// <summary>Хэштеги Страницы, если заданы, иначе общие.</summary>
        static string HashtagsFor(FacebookPageSettings page, AppSettings appSettings) =>
            string.IsNullOrWhiteSpace(page.DefaultHashtags) ? appSettings.DefaultHashtags : page.DefaultHashtags;

        // ─────────────────────────────────────────────────────────────────────
        //  Вывод настроек
        // ─────────────────────────────────────────────────────────────────────

        static void DisplaySettings(List<FacebookPageSettings> pages, AppSettings appSettings, ServerSettings server)
        {
            var pagesTable = new Table().Border(TableBorder.Rounded);
            pagesTable.AddColumn("[yellow]Страница Facebook[/]");
            pagesTable.AddColumn("[green]TikTok источники[/]");
            pagesTable.AddColumn("[cyan]Статус[/]");

            foreach (var page in pages)
            {
                var status = page.NotReadyReason is { } notReady ? $"[red]❌ {Esc(notReady)}[/]" : "[green]✓ Активна[/]";
                var tiktoks = string.Join(", ", page.TikTokUsernames.Select(u => $"@{Esc(u)}"));
                pagesTable.AddRow(Esc(page.PageName), tiktoks, status);
            }

            AnsiConsole.Write(pagesTable);
            AnsiConsole.WriteLine();

            var configTable = new Table().Border(TableBorder.Rounded);
            configTable.AddColumn("[yellow]Параметр[/]");
            configTable.AddColumn("[green]Значение[/]");
            configTable.AddRow("⏱️  Интервал проверки (полный цикл)", $"{appSettings.CheckIntervalMinutes} минут");
            configTable.AddRow("⏳ Задержка между проверками", $"{appSettings.TikTokCheckDelaySeconds} секунд");
            configTable.AddRow("🌐 Публичный адрес", Esc(string.IsNullOrWhiteSpace(server.PublicUrl) ? "не задан" : server.PublicUrl));
            configTable.AddRow("📤 Как отдаём файл", Esc(server.UploadMode));
            configTable.AddRow("✍️  Обязательная подпись", Esc(string.IsNullOrWhiteSpace(appSettings.RequiredCaptionSuffix) ? "нет" : appSettings.RequiredCaptionSuffix));
            configTable.AddRow("#️⃣  Хэштеги (если своих нет)", Esc(string.IsNullOrWhiteSpace(appSettings.DefaultHashtags) ? "не дописываются" : appSettings.DefaultHashtags));
            foreach (var page in pages.Where(p => !string.IsNullOrWhiteSpace(p.DefaultHashtags)))
            {
                configTable.AddRow($"#️⃣  Хэштеги {Esc(page.PageName)}", Esc(page.DefaultHashtags!));
            }
            configTable.AddRow("🚦 Лимит на Страницу за 24ч", $"{appSettings.Publishing.MaxPostsPerPagePerDay} (потолок Facebook: {appSettings.Publishing.ApiHardLimitPer24h})");
            configTable.AddRow("💾 История", "Последние 5 видео с timestamp");

            AnsiConsole.Write(configTable);
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine("[yellow]ℹ️  Алгоритм проверки: «карусель» — проходим по позициям в массивах[/]");
            AnsiConsole.MarkupLine("[yellow]   Пример: Страница#1→TikTok[[0]], Страница#2→TikTok[[0]], ..., Страница#1→TikTok[[1]], ...[/]");
            AnsiConsole.MarkupLine("[yellow]   Защита: история последних 5 видео с timestamp предотвращает дубликаты[/]\n");
        }

        /// <summary>
        /// Экранирование для Spectre.Console. Названия Страниц и описания роликов
        /// приходят снаружи, и одна квадратная скобка в них роняла бы вывод
        /// исключением про неразобранную разметку.
        /// </summary>
        static string Esc(string? s) => Markup.Escape(s ?? string.Empty);

        // ─────────────────────────────────────────────────────────────────────
        //  DI
        // ─────────────────────────────────────────────────────────────────────

        static IFacebookReelsService CreateFacebookService(
            IServiceProvider serviceProvider,
            IConfiguration config,
            FacebookPageSettings page,
            ServerSettings server)
        {
            var logger = serviceProvider.GetRequiredService<ILogger<FacebookReelsService>>();
            var httpClient = serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("facebook");

            var settings = Microsoft.Extensions.Options.Options.Create(new FacebookSettings
            {
                PageId = page.PageId,
                PageAccessToken = page.PageAccessToken,
                ApiVersion = config["Facebook:ApiVersion"] ?? "v26.0",
                ProcessingTimeoutSeconds = int.TryParse(config["Facebook:ProcessingTimeoutSeconds"], out var t) ? t : 300,
                UploadMode = string.IsNullOrWhiteSpace(server.UploadMode) ? "bytes" : server.UploadMode
            });

            return new FacebookReelsService(settings, logger, httpClient);
        }

        static IServiceCollection ConfigureServices()
        {
            var services = new ServiceCollection();

            var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";

            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: true)
                .Build();

            services.AddSingleton<IConfiguration>(configuration);

            services.Configure<TikTokMonitorSettings>(configuration.GetSection("TikTokMonitor"));
            services.Configure<UniquifierSettings>(configuration.GetSection("Uniquifier"));
            services.Configure<PublishingSettings>(configuration.GetSection("Publishing"));

            services.AddLogging(builder =>
            {
                builder.AddConfiguration(configuration.GetSection("Logging"));
                builder.AddConsole();

                // БЕЗОПАСНОСТЬ, а не борьба с многословностью.
                //
                // Штатный логгер HttpClient печатает КАЖДЫЙ запрос целиком,
                // вместе со строкой запроса. А маркер доступа ездит именно
                // в строке запроса — то есть в логи попадал бы живой токен от
                // Страницы, в открытом виде. Логи смотрят через
                // «docker compose logs», копируют в переписку, показывают
                // знакомым, и любой, кто увидит такую строку, сможет публиковать
                // от имени Страницы, пока токен не истечёт.
                //
                // Ставится ПОСЛЕ AddConfiguration, чтобы это нельзя было
                // случайно отменить из appsettings.json.
                builder.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
            });

            // Заливка видео байтами на медленном канале идёт долго, а таймаут
            // HttpClient по умолчанию — 100 секунд. С ним ролик на 30 МБ рвался
            // бы на середине, и выглядело бы это как отказ Facebook.
            services.AddHttpClient("facebook")
                .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMinutes(20));

            services.AddHttpClient();

            services.AddSingleton<ITikTokMonitorService, TikTokMonitorService>();
            services.AddSingleton<IVideoUniquifierService, VideoUniquifierService>();
            services.AddSingleton<IPublishThrottle, PublishThrottleService>();

            return services;
        }
    }
}
