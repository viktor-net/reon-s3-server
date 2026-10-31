#!/usr/bin/env bash
set -euo pipefail

SWIFT_DIR=/etc/swift
DEVICE_DIR=/srv/node/d1
LOG_FILE=/var/log/swift/swift.log

log() {
    echo "[entrypoint] $*"
}

# Data can come from a volume owned by another UID; fix it up on every start.
mkdir -p "$DEVICE_DIR" /var/cache/swift /var/log/swift /run/swift
chown -R swift:swift /srv/node /var/cache/swift /var/log/swift /run/swift
: >> "$LOG_FILE"

# Start rsyslog first so /dev/log exists before any Swift daemon logs.
# A stale pid file survives container restarts in /run, so remove it first.
log "Starting rsyslog"
rm -f /run/rsyslogd.pid
rsyslogd -n -i /run/rsyslogd.pid -f /etc/rsyslog.conf &
for _ in $(seq 1 50); do
    [ -S /dev/log ] && break
    sleep 0.1
done

# One replica on a single device is enough for a local all-in-one node.
# The rings are deterministic, so regenerating them after a container
# recreation still points at the same on-disk data.
if [ ! -f "$SWIFT_DIR/account.ring.gz" ] || \
   [ ! -f "$SWIFT_DIR/container.ring.gz" ] || \
   [ ! -f "$SWIFT_DIR/object.ring.gz" ]; then
    log "Creating account/container/object rings"
    cd "$SWIFT_DIR"
    rm -f ./*.builder ./*.ring.gz ./backup/*.builder ./backup/*.ring.gz 2>/dev/null || true

    swift-ring-builder account.builder create 10 1 1
    swift-ring-builder account.builder add --region 1 --zone 1 \
        --ip 127.0.0.1 --port 6002 --device d1 --weight 100
    swift-ring-builder account.builder rebalance

    swift-ring-builder container.builder create 10 1 1
    swift-ring-builder container.builder add --region 1 --zone 1 \
        --ip 127.0.0.1 --port 6001 --device d1 --weight 100
    swift-ring-builder container.builder rebalance

    swift-ring-builder object.builder create 10 1 1
    swift-ring-builder object.builder add --region 1 --zone 1 \
        --ip 127.0.0.1 --port 6000 --device d1 --weight 100
    swift-ring-builder object.builder rebalance

    chown swift:swift "$SWIFT_DIR"/*.builder "$SWIFT_DIR"/*.ring.gz
fi

if ! pgrep -x memcached >/dev/null 2>&1; then
    log "Starting memcached"
    memcached -u memcache -l 127.0.0.1 -p 11211 -m 128 -d
fi

log "Starting Swift services"
# swift-init returns non-zero when an optional daemon (e.g. the reconciler)
# has no config file, even though all configured services started fine.
swift-init all start || true

for _ in $(seq 1 60); do
    if curl -fsS http://127.0.0.1:8080/healthcheck >/dev/null 2>&1; then
        break
    fi
    sleep 1
done

if ! curl -fsS http://127.0.0.1:8080/healthcheck >/dev/null 2>&1; then
    log "Swift proxy did not become healthy; recent logs:"
    tail -n 100 "$LOG_FILE" || true
    exit 1
fi

tail_pid=
cleanup() {
    log "Stopping Swift services"
    [ -n "$tail_pid" ] && kill "$tail_pid" 2>/dev/null || true
    swift-init all stop || true
    pkill -x rsyslogd || true
}
trap cleanup TERM INT

log "Swift is up: http://127.0.0.1:8080 (S3 API and native Swift API)"
tail -n +1 -F "$LOG_FILE" &
tail_pid=$!
wait "$tail_pid" || true
