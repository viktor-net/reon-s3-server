#!/usr/bin/env bash
# Заполняет dev-стенд демо-данными для Reon S3 Server.
#
# Что делает (в порядке записи из README сервиса):
#   1. считает план: fileId, ключ files/{fileId}{ext}, размер и SHA-256;
#   2. вставляет строки files в статусе Pending;
#   3. кладёт объекты в S3 с metadata reon-file-id и reon-sha256;
#   4. переводит строки в Ready, сохраняя ETag и версию объекта из S3;
#   5. добавляет строки в остальных статусах, квоты и журнал операций.
#
# Требования: запущенный стенд (docker compose), Docker Compose v2,
# контейнеры postgres и swift, boto3 внутри контейнера swift.
#
# Примеры:
#   bash scripts/seed/seed.sh            # файловый домен и бакеты фасада
#   bash scripts/seed/seed.sh files      # только files + S3 основного бакета
#   bash scripts/seed/seed.sh admin      # только бакеты, квоты и аудит
set -euo pipefail

# Git Bash (MSYS) иначе переписывает /tmp/... в C:/... при вызове docker.exe,
# и путь внутри контейнера ломается.
export MSYS_NO_PATHCONV=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

PG_SERVICE="${SEED_PG_SERVICE:-postgres}"
SWIFT_SERVICE="${SEED_SWIFT_SERVICE:-swift}"
PG_USER="${SEED_PG_USER:-reon}"
PG_DB="${SEED_PG_DB:-reon}"
MAIN_BUCKET="${SEED_BUCKET:-reon-files}"
KEY_PREFIX="${SEED_KEY_PREFIX:-files/}"
OWNER="${SEED_OWNER:-system}"
S3_ENDPOINT="${SEED_S3_ENDPOINT:-http://127.0.0.1:8080}"
S3_ACCESS_KEY="${SEED_S3_ACCESS_KEY:-test:tester}"
S3_SECRET_KEY="${SEED_S3_SECRET_KEY:-testing}"
S3_REGION="${SEED_S3_REGION:-us-east-1}"
REMOTE_DIR="/tmp/reon-seed"

usage() {
  cat <<'EOF'
Заполнение dev-стенда демо-данными Reon S3.

Использование:
  bash scripts/seed/seed.sh [all|files|admin]

  all    файловый домен, бакеты фасада, квоты и журнал (по умолчанию)
  files  только files в Postgres и объекты в бакете приложения
  admin  только бакеты фасада, их квоты и audit_events

Переменные окружения:
  SEED_COMPOSE_DIR    каталог стенда с docker-compose.yml (иначе автопоиск)
  SEED_PG_SERVICE     сервис Postgres в compose (postgres)
  SEED_SWIFT_SERVICE  сервис Swift в compose (swift)
  SEED_PG_USER        пользователь БД (reon)
  SEED_PG_DB          база данных (reon)
  SEED_BUCKET         бакет приложения (reon-files)
  SEED_KEY_PREFIX     префикс ключей (files/)
  SEED_OWNER          значение поля owner (system)
  SEED_S3_ENDPOINT    S3 endpoint изнутри контейнера Swift (http://127.0.0.1:8080)
  SEED_S3_ACCESS_KEY  access key (test:tester)
  SEED_S3_SECRET_KEY  secret key (testing)
  SEED_S3_REGION      регион (us-east-1)

Удалить демо-данные: bash scripts/seed/clean.sh
EOF
}

die() {
  echo "Ошибка: $*" >&2
  exit 1
}

has_stand_services() {
  # Сервисы верхнего уровня: postgres и swift должны быть в блоке services.
  local services
  services="$(awk '
    /^[A-Za-z]/ { in_services = ($0 ~ /^services:/) }
    in_services && /^  [A-Za-z0-9_.-]+:/ {
      sub(/^  /, ""); sub(/:.*/, ""); print
    }
  ' "$1")"

  grep -qx postgres <<<"$services" && grep -qx swift <<<"$services"
}

find_compose_dir() {
  if [[ -n "${SEED_COMPOSE_DIR:-}" ]]; then
    [[ -f "$SEED_COMPOSE_DIR/docker-compose.yml" ]] || die \
      "в SEED_COMPOSE_DIR нет docker-compose.yml: $SEED_COMPOSE_DIR"
    (cd "$SEED_COMPOSE_DIR" && pwd)
    return
  fi

  local candidate
  for candidate in "$PWD" "$REPO_ROOT"; do
    [[ -f "$candidate/docker-compose.yml" ]] || continue
    if has_stand_services "$candidate/docker-compose.yml"; then
      (cd "$candidate" && pwd)
      return
    fi
  done

  return 1
}

SCOPE="${1:-all}"
case "$SCOPE" in
  -h|--help|help)
    usage
    exit 0
    ;;
  all|files|admin)
    ;;
  *)
    usage >&2
    exit 2
    ;;
esac

command -v docker >/dev/null 2>&1 || die "Docker не найден в PATH."
docker compose version >/dev/null 2>&1 || die "Docker Compose v2 не найден."

COMPOSE_DIR="$(find_compose_dir)" || die \
  "не найден docker-compose.yml стенда. Укажите каталог в SEED_COMPOSE_DIR."

dc() {
  (cd "$COMPOSE_DIR" && docker compose "$@")
}

running="$(dc ps --services --status running 2>/dev/null || true)"
grep -qx "$PG_SERVICE" <<<"$running" || die \
  "сервис '$PG_SERVICE' не запущен в $COMPOSE_DIR (docker compose up -d)."
grep -qx "$SWIFT_SERVICE" <<<"$running" || die \
  "сервис '$SWIFT_SERVICE' не запущен в $COMPOSE_DIR (docker compose up -d)."

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

echo "Стенд: $COMPOSE_DIR"
echo "Бакет приложения: $MAIN_BUCKET, префикс ключей: $KEY_PREFIX"

# Скрипт кладём в контейнер, чтобы stdin остался свободным для плана в JSON.
dc exec -T "$SWIFT_SERVICE" sh -c "mkdir -p $REMOTE_DIR && cat > $REMOTE_DIR/seed_data.py" \
  < "$SCRIPT_DIR/seed_data.py"

py() {
  dc exec -T \
    -e SEED_BUCKET="$MAIN_BUCKET" \
    -e SEED_KEY_PREFIX="$KEY_PREFIX" \
    -e SEED_OWNER="$OWNER" \
    -e SEED_S3_ENDPOINT="$S3_ENDPOINT" \
    -e SEED_S3_ACCESS_KEY="$S3_ACCESS_KEY" \
    -e SEED_S3_SECRET_KEY="$S3_SECRET_KEY" \
    -e SEED_S3_REGION="$S3_REGION" \
    "$SWIFT_SERVICE" python3 "$REMOTE_DIR/seed_data.py" "$@"
}

psql_stdin() {
  dc exec -T "$PG_SERVICE" \
    psql -v ON_ERROR_STOP=1 -q -U "$PG_USER" -d "$PG_DB" -f -
}

seed_files() {
  echo
  echo "== Файловый домен =="
  echo "1/4 план: fileId, ключи, размер и SHA-256"
  py manifest > "$TMP_DIR/plan.json"

  echo "2/4 Postgres: строки files в статусе Pending"
  py sql-pending < "$TMP_DIR/plan.json" | psql_stdin

  echo "3/4 S3: объекты и metadata reon-file-id / reon-sha256"
  py upload files < "$TMP_DIR/plan.json" > "$TMP_DIR/uploaded.json"

  echo "4/4 Postgres: строки Ready с ETag и версией объекта"
  py sql-ready < "$TMP_DIR/uploaded.json" | psql_stdin

  echo "   Postgres: строки в остальных статусах жизненного цикла"
  py sql-statuses | psql_stdin
}

seed_admin() {
  echo
  echo "== Бакеты административного фасада =="
  echo "1/2 S3: бакеты, объекты и маркеры папок"
  py manifest > "$TMP_DIR/admin-plan.json"
  py upload admin < "$TMP_DIR/admin-plan.json" > /dev/null

  echo "2/2 Postgres: квоты bucket_settings и журнал audit_events"
  py sql-admin | psql_stdin
}

if [[ "$SCOPE" == "all" || "$SCOPE" == "files" ]]; then
  seed_files
fi

if [[ "$SCOPE" == "all" || "$SCOPE" == "admin" ]]; then
  seed_admin
fi


echo
echo "Готово. Проверка:"
echo "  curl 'http://localhost:8090/api/files?pageSize=5'"
echo "  curl 'http://localhost:8090/api/admin/overview'"
echo "  http://localhost:8090/scalar/api"
echo
echo "Удалить демо-данные: bash scripts/seed/clean.sh"
