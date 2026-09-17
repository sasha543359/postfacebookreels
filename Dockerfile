FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY *.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app

# python3 + ffmpeg (уникализатор) + шрифт с кириллицей (для экранной подписи)
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        python3 python3-venv ffmpeg fonts-dejavu-core ca-certificates curl && \
    apt-get clean && rm -rf /var/lib/apt/lists/*

# yt-dlp ставим из pip в venv, а не бинарником с GitHub. Причина: бинарник
# вмораживается в образ на дату сборки, а TikTok ломает извлечение примерно раз
# в месяц. Из venv версию можно обновить на старте контейнера (см. ENTRYPOINT),
# не пересобирая образ.
#
# ВНИМАНИЕ НА «pin-curl-cffi». Это не украшение и не осторожность —
# без него TikTok не качается.
#
# curl-cffi подменяет отпечаток TLS, изображая настоящий браузер. Начиная с
# версии 0.16.1 в ней появилась цель «chrome-150», и yt-dlp всегда выбирает
# самую свежую из доступных. А TikTok именно chrome-150 блокирует наглухо
# (открытая проблема yt-dlp #17604): в логах это выглядит как «Unexpected
# response from webpage request», то есть как будто сломался сам yt-dlp.
#
# Набор «pin-curl-cffi» — это официальный extra самого yt-dlp, который
# закрепляет curl-cffi на 0.16.0. Там свежайшая цель — chrome-146, и она
# работает. Поэтому:
#   • не меняй эту строку на просто "yt-dlp[default]";
#   • не делай "pip install -U curl-cffi" — это вернёт 0.16.x и снова всё сломает.
RUN python3 -m venv /opt/ytdlp && \
    /opt/ytdlp/bin/pip install --no-cache-dir --upgrade pip && \
    /opt/ytdlp/bin/pip install --no-cache-dir "yt-dlp[default,curl-cffi,pin-curl-cffi]" && \
    ln -sf /opt/ytdlp/bin/yt-dlp /usr/local/bin/yt-dlp

RUN mkdir -p /var/www/videos/covers

COPY --from=build /app .
# скрипт уникализатора (вызывается из VideoUniquifierService через python3)
COPY uniquify.py /app/uniquify.py

# На каждом старте подтягиваем свежий yt-dlp: TikTok ломается регулярно, а
# yt-dlp чинится за считанные дни, так что достаточно перезапустить контейнер.
# Нет сети или pip отвалился — не страшно, продолжаем на версии из образа.
#
# Обновляется ТОЛЬКО сам yt-dlp, без зависимостей: «--upgrade» без
# «--upgrade-strategy only-if-needed» утянул бы за собой свежий curl-cffi и
# вернул бы заблокированный TikTok'ом chrome-150. Поэтому здесь ещё и
# «curl-cffi==0.16.0» следом — закрепляем обратно, если что-то её сдвинуло.
#
# «"$@"» в конце и «--» последним элементом — чтобы аргументы контейнера
# доезжали до программы. Без них «docker compose run --rm app --check» молча
# запускал бы обычный бесконечный цикл, то есть проверить настройки внутри
# контейнера было бы нечем.
ENTRYPOINT ["/bin/sh", "-c", "\
echo \"[entrypoint] yt-dlp из образа: $(yt-dlp --version 2>/dev/null || echo 'не найден')\"; \
if timeout 180 /opt/ytdlp/bin/pip install --no-cache-dir --quiet --upgrade --upgrade-strategy only-if-needed yt-dlp 'curl-cffi==0.16.0'; then \
  echo \"[entrypoint] yt-dlp обновлён до: $(yt-dlp --version)\"; \
else \
  echo '[entrypoint] обновить yt-dlp не удалось — работаем на версии из образа'; \
fi; \
echo \"[entrypoint] curl-cffi: $(/opt/ytdlp/bin/python -c 'import curl_cffi; print(curl_cffi.__version__)' 2>/dev/null || echo 'нет')\"; \
if yt-dlp --list-impersonate-targets 2>/dev/null | grep -q 'chrome-150'; then \
  echo '[entrypoint] ВНИМАНИЕ: доступна цель chrome-150 — TikTok её блокирует. Нужна curl-cffi 0.16.0'; \
fi; \
exec dotnet FacebookReelsPublisher.dll \"$@\"", "--"]
