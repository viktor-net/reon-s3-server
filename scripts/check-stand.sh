#!/usr/bin/env bash
# Проверяет, что стенд можно поднять на этой машине, и готовит .env.
#
# Что проверяет:
#   1. docker и docker compose v2 доступны, демон запущен;
#   2. .env есть (создаёт из .env.example, если файла нет);
#   3. на месте файлы, которые монтируются в контейнеры;
#   4. образ Swift S3: если его нет — предупреждает, что он соберётся
#      при первом `docker compose up -d --build`;
#   5. порты стенда не заняты чужими процессами (если стенд ещё не поднят).
#
# Запуск:
#   bash scripts/check-stand.sh
#
# Код возврата: 0 — можно запускать, 1 — есть блокирующая проблема.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"

failed=0
warnings=0

ok() { printf '  [ok]   %s\n' "$*"; }
warn() { printf '  [warn] %s\n' "$*"; warnings=$((warnings + 1)); }
fail() { printf '  [fail] %s\n' "$*"; failed=1; }
section() { printf '\n%s\n' "$*"; }

section 'Docker'

if ! command -v docker >/dev/null 2>&1; then
    fail 'docker не найден в PATH. Установите Docker Engine или Docker Desktop.'
else
    ok "docker: $(docker --version)"

    if ! compose_version="$(docker compose version --short 2>/dev/null)"; then
        fail 'docker compose v2 недоступен. Нужен плагин compose (docker compose, не docker-compose).'
    else
        ok "docker compose: $compose_version"
    fi

    if docker info --format '{{.ServerVersion}}' >/dev/null 2>&1; then
        ok 'Docker-демон запущен'
    else
        fail 'Docker-демон не отвечает. Запустите Docker Desktop (или службу docker) и повторите.'
    fi
fi

section 'Конфигурация'

if [ -f .env ]; then
    ok '.env на месте'
elif [ -f .env.example ]; then
    cp .env.example .env
    ok '.env создан из .env.example (значения по умолчанию — только для локального стенда)'
else
    fail 'Нет ни .env, ни .env.example — скопируйте репозиторий целиком.'
fi

required_files=(
    docker-compose.yml
    docker-compose.api.yml
    docker/swift/Dockerfile
    docker/swift/entrypoint.sh
    docker/swagger/openapi.yaml
    docker/swagger/nginx.conf
    docker/filestash/config.json
    docker/otel-collector/config.yaml
)
missing=()
for file in "${required_files[@]}"; do
    [ -e "$file" ] || missing+=("$file")
done
if [ ${#missing[@]} -eq 0 ]; then
    ok 'файлы для монтирования и сборки на месте'
else
    fail "не хватает файлов: ${missing[*]}"
fi

section 'Образ Swift S3'

# SWIFT_IMAGE из .env переопределяет имя образа; по умолчанию — тег из compose.
swift_image='swift-s3api-aio:2.33'
if [ -f .env ]; then
    override="$(sed -n 's/^[[:space:]]*SWIFT_IMAGE[[:space:]]*=[[:space:]]*//p' .env | head -n 1)"
    override="${override%\"}"; override="${override#\"}"
    override="${override%\'}"; override="${override#\'}"
    [ -n "$override" ] && swift_image="$override"
fi

if [ "$failed" -eq 1 ]; then
    warn 'проверка образа пропущена: Docker недоступен'
elif docker image inspect "$swift_image" >/dev/null 2>&1; then
    ok "образ $swift_image уже собран"
else
    warn "образа $swift_image нет — он соберётся при первом запуске с --build (5-10 минут, нужен интернет)"
fi

section 'Порты'

if [ "$failed" -eq 1 ]; then
    warn 'проверка портов пропущена: Docker недоступен'
elif docker compose ps --status running --quiet 2>/dev/null | grep -q .; then
    ok 'стенд уже запущен — проверка портов не нужна'
else
    # Bash-редирект в /dev/tcp открывает соединение; отказ означает свободный порт.
    port_busy() {
        (exec 3<>"/dev/tcp/127.0.0.1/$1") >/dev/null 2>&1
    }

    busy=()
    for entry in 5433:Postgres 8080:'Swift S3' 8081:'Swagger UI' 8082:Filestash \
                 8090:API 4317:'OTLP gRPC' 4318:'OTLP HTTP' 13133:'OTel health'; do
        port="${entry%%:*}"
        name="${entry#*:}"
        port_busy "$port" && busy+=("$port ($name)")
    done

    if [ ${#busy[@]} -eq 0 ]; then
        ok 'порты свободны'
    else
        warn "заняты порты: ${busy[*]}. Остановите занимающие их процессы или поменяйте порты в docker-compose.yml"
    fi
fi

printf '\n'
if [ "$failed" -eq 1 ]; then
    printf 'Стенд запускать нельзя: исправьте ошибки выше.\n'
    exit 1
fi

printf 'Можно запускать:\n'
printf '  docker compose up -d --build\n'
if [ "$warnings" -gt 0 ]; then
    printf '(предупреждений: %d — они не блокируют запуск)\n' "$warnings"
fi
exit 0
