#!/bin/bash
#
# Разворачивает публикатор на чистой Ubuntu (DigitalOcean и любой другой VPS).
# Запускать на СЕРВЕРЕ под root:
#
#   bash <(curl -fsSL https://raw.githubusercontent.com/ТВОЙ_НИК/postfacebookreels/master/setup.sh)
#
# или, если папка проекта уже на сервере:  bash setup.sh
#
# Скрипт НЕ запускает программу в конце — сначала надо вписать свои токены
# в appsettings.json. Что делать дальше, он печатает сам.
#
# Чем он полезнее ручной установки: docker-compose.yml для сервера пишется
# скриптом, а не копируется из мессенджера. Именно на ручном копировании
# слетают отступы и Docker отвечает «services must be a mapping».

set -euo pipefail

REPO="${REPO:-https://github.com/sasha543359/postfacebookreels.git}"
BRANCH="${BRANCH:-master}"
DIR="${DIR:-/opt/facebook-reels-publisher}"
MEDIA="${MEDIA:-/var/www/videos}"

if [ "$(id -u)" != "0" ]; then
  echo "Запускай под root: sudo bash setup.sh" >&2
  exit 1
fi

echo "=== 1/7  Пакеты ==="
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq nginx git curl ca-certificates

echo "=== 2/7  Docker ==="
if ! command -v docker >/dev/null 2>&1; then
  curl -fsSL https://get.docker.com | sh
fi
systemctl enable --now docker
# На некоторых образах плагин compose ставится отдельно.
docker compose version >/dev/null 2>&1 || apt-get install -y -qq docker-compose-plugin

echo "=== 3/7  Подкачка ==="
# Сборка .NET на дроплете с 1-2 ГБ памяти падает с кодом 137 (OOM). Пара
# гигабайт подкачки решает это раз и навсегда и ничего не стоит, когда память
# есть.
ram_mb=$(free -m | awk '/^Mem:/ {print $2}')
if [ "$ram_mb" -lt 3000 ] && [ ! -f /swapfile ]; then
  fallocate -l 2G /swapfile
  chmod 600 /swapfile
  mkswap /swapfile >/dev/null
  swapon /swapfile
  grep -q '^/swapfile' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
  echo "    добавлено 2 ГБ подкачки (памяти было ${ram_mb} МБ)"
else
  echo "    не требуется"
fi

echo "=== 4/7  Папки ==="
mkdir -p "$MEDIA/covers"
chmod -R 755 "$MEDIA"

# Веб-консоль DigitalOcean вставляет текст (Ctrl+Shift+V) в «скобках»
# ^[[200~ … ~, и вставленная команда падает с «command not found».
# Проверено 18.09.2026. Отключаем этот режим для всех будущих сеансов root.
if ! grep -q "enable-bracketed-paste" /root/.bashrc 2>/dev/null; then
  cat >> /root/.bashrc << 'BASHRC'
# Веб-консоль DigitalOcean вставляет текст с мусором ^[[200~ — отключаем этот режим.
[[ $- == *i* ]] && bind "set enable-bracketed-paste off"
BASHRC
fi

echo "=== 5/7  Nginx ==="
# Отдаёт скачанные видео наружу — по этой ссылке Facebook забирает файл.
# Нужен только в режимах "url" и "auto". В режиме "bytes" (стоит по умолчанию)
# видео заливается файлом и Nginx не используется, но и не мешает.
#
# Про robots.txt это не перестраховка: Facebook официально отказывается брать
# файл с сайта, закрытого через robots.txt, и требует пускать робота
# «facebookexternalhit/1.1». Пустой ответ 204 означает «запретов нет».
cat > /etc/nginx/sites-available/facebook-reels-publisher << NGINX
server {
    listen 80;
    server_name _;

    location /videos/ {
        alias $MEDIA/;
        autoindex off;
        types {
            video/mp4 mp4;
            image/jpeg jpg jpeg;
            image/png  png;
        }
    }

    location = /robots.txt {
        add_header Content-Type text/plain;
        return 204;
    }

    client_max_body_size 100M;
}
NGINX
rm -f /etc/nginx/sites-enabled/default
ln -sf /etc/nginx/sites-available/facebook-reels-publisher /etc/nginx/sites-enabled/
nginx -t
systemctl restart nginx
systemctl enable nginx >/dev/null 2>&1

echo "=== 6/7  Код ==="
if [ -d "$DIR/.git" ]; then
  git -C "$DIR" pull --ff-only
elif [ -f "$DIR/FacebookReelsPublisher.csproj" ]; then
  echo "    папка уже на месте (положена вручную) — оставляем как есть"
else
  git clone --branch "$BRANCH" "$REPO" "$DIR"
fi

echo "=== 7/7  docker-compose.yml для сервера ==="
# В репозитории лежит вариант со своим Nginx внутри — он воюет за порт 80
# с тем, что мы только что настроили. Серверу нужен вариант «только программа»
# с папкой, примонтированной с хоста.
cat > "$DIR/docker-compose.yml" << COMPOSE
services:
  app:
    build: .
    restart: always
    volumes:
      - $MEDIA:$MEDIA
COMPOSE

ip=$(curl -fsS --max-time 5 https://api.ipify.org 2>/dev/null || hostname -I | awk '{print $1}')

# Проверяем, открываются ли видео по ссылке. Это только для сведения: по
# умолчанию стоит "UploadMode": "bytes", которому ссылка не нужна. Результат
# важен, лишь если режим переключат на "url" или "auto". Проверка идёт с самого
# сервера на его же адрес, поэтому закрытый снаружи порт может и не заметить.
echo "проверка" > "$MEDIA/setup-probe.txt"
probe_ok=no
curl -fsS --max-time 8 "http://$ip/videos/setup-probe.txt" >/dev/null 2>&1 && probe_ok=yes
rm -f "$MEDIA/setup-probe.txt"

cat << FINAL

  Готово. Осталось вписать свои данные — пока в файле заглушки,
  программа ничего не публикует.

  1) Открыть настройки:

       nano $DIR/appsettings.json

     Что менять (в файле всё подписано комментариями):
       • PageId и PageAccessToken — вместо заглушек ВСТАВЬ_…;
         ненужную Страницу не трогай: с заглушкой она пропускается;
       • список TikTokUsernames.
     PublicUrl трогать не нужно: по умолчанию стоит "UploadMode": "bytes",
     видео заливается файлом, и адрес не используется.

     Сохранить: Ctrl+O, Enter, Ctrl+X.

  2) Собрать и проверить, ничего не публикуя (токены + список авторов).
     Первая сборка — 2-5 минут:

       cd $DIR
       docker compose build && docker compose run --rm app --check

  3) Если проверка зелёная — запустить и смотреть логи:

       docker compose up -d --build
       docker compose logs -f app

FINAL

if [ "$probe_ok" = "yes" ]; then
  cat << OK
  Проверка ссылки: http://$ip/videos/ отвечает (проверка шла с самого
  сервера — закрытый снаружи порт она может и не заметить).

  Делать ничего не надо: по умолчанию стоит "UploadMode": "bytes" —
  проверенный режим, видео заливается в Facebook файлом. Адрес понадобится,
  только если сам переключишь режим на "url" или "auto": тогда впиши
  "PublicUrl": "http://$ip".

OK
else
  cat << WARN
  Проверка ссылки: http://$ip/videos/ не ответил.
  Причины обычно две: провайдер закрыл порт 80 или ещё не поднялся firewall.

  Это НЕ мешает работе, делать ничего не надо: по умолчанию стоит
  "UploadMode": "bytes" — проверенный режим, видео заливается в Facebook
  файлом, публичный адрес и Nginx ему не нужны. Режимы "url" и "auto" на этом
  сервере работать не будут, пока адрес не откроется снаружи.

WARN
fi

cat << TAIL
  Помни: appsettings.json вшивается в образ при сборке. После любой правки
  нужен именно "docker compose up -d --build", просто "up -d" поднимет
  старые настройки.

TAIL
