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

echo "=== 5/7  Nginx ==="
# Отдаёт скачанные видео наружу — по этой ссылке Facebook забирает файл.
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

# Сразу проверяем, что сервер реально виден снаружи по этому адресу. Если нет —
# программа всё равно будет работать (режим "bytes"), но лучше узнать об этом
# сейчас, чем по невнятной ошибке публикации через час.
echo "проверка" > "$MEDIA/setup-probe.txt"
probe_ok=no
curl -fsS --max-time 8 "http://$ip/videos/setup-probe.txt" >/dev/null 2>&1 && probe_ok=yes
rm -f "$MEDIA/setup-probe.txt"

cat << FINAL

  Готово. Осталось вписать свои данные — иначе программа стартует
  с заглушками и будет отвечать ошибкой авторизации.

  1) Открыть настройки:

       nano $DIR/appsettings.json

     Что менять (в файле всё подписано комментариями):
       • PageId и PageAccessToken у каждой Страницы;
         у неиспользуемых Страниц PageAccessToken должен быть пустой строкой "";
       • список TikTokUsernames;
       • "PublicUrl": "http://$ip"   ← вот этот адрес, внизу файла.

     Сохранить: Ctrl+O, Enter, Ctrl+X.

  2) Собрать и запустить:

       cd $DIR
       docker compose up -d --build
       docker compose logs -f app

  Проверить настройки, ничего не публикуя (токены + список авторов):

       cd $DIR && docker compose run --rm app --check

FINAL

if [ "$probe_ok" = "yes" ]; then
  cat << OK
  Сервер снаружи виден: http://$ip/videos/ отвечает.
  Оставляй "UploadMode": "auto" — Facebook будет забирать видео по ссылке.

OK
else
  cat << WARN
  ВНИМАНИЕ: снаружи http://$ip/videos/ не ответил.
  Причины обычно две: провайдер закрыл порт 80 или ещё не поднялся firewall.

  Это НЕ мешает работе. Поставь в appsettings.json:

       "UploadMode": "bytes"

  Тогда программа будет заливать видео в Facebook напрямую, и публичный адрес
  не понадобится вовсе — вместе с ним не понадобится и Nginx.

WARN
fi

cat << TAIL
  Помни: appsettings.json вшивается в образ при сборке. После любой правки
  нужен именно "docker compose up -d --build", просто "up -d" поднимет
  старые настройки.

TAIL
