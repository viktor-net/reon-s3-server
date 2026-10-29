#!/usr/bin/env bash
# Удаляет демо-данные, созданные scripts/seed/seed.sh.
#
# Удаляются только объекты с детерминированными ключами сида, строки files и
# audit_events с детерминированными id, квоты демо-бакетов. Остальные данные
# стенда не затрагиваются. Демо-бакеты удаляются, только если они опустели.
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

command -v docker >/dev/null 2>&1 || die "Docker не найден в PATH."
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

echo "Стенд: $COMPOSE_DIR"

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

echo "1/2 S3: удаление объектов и пустых демо-бакетов"
py clean-s3

echo "2/2 Postgres: удаление строк files, audit_events и квот"
py sql-clean | psql_stdin

echo
echo "Демо-данные удалены."
