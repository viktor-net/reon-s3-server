# S3-совместимое хранилище стенда: OpenStack Swift + s3api, all-in-one.
#
# Файл — копия сборки из репозитория reon-s3-dev (образ swift-s3api-aio:2.33).
# Хранится здесь, чтобы стенд поднимался из одного репозитория без внешних
# зависимостей: docker compose up -d --build собирает образ локально.
# При правках конфигов Swift синхронизируйте изменения с reon-s3-dev.
FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive \
    PYTHONUNBUFFERED=1

# Real OpenStack Swift services (proxy + account/container/object servers),
# memcached for the proxy cache, and rsyslog so every Swift daemon has a
# working /dev/log inside the container.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        swift \
        swift-proxy \
        swift-account \
        swift-container \
        swift-object \
        python3-keystoneauth1 \
        python3-boto3 \
        memcached \
        rsyslog \
        curl \
        ca-certificates \
        bash \
 && rm -rf /var/lib/apt/lists/* \
 && rm -f /etc/swift/*.conf

COPY etc/swift/ /etc/swift/
COPY etc/rsyslog.conf /etc/rsyslog.conf
# В reon-s3-dev скрипт лежит в docker/entrypoint.sh; здесь контекст сборки —
# сам каталог docker/swift, поэтому путь короче.
COPY entrypoint.sh /usr/local/bin/entrypoint.sh
COPY smoky_tests/ /opt/smoky_tests/

RUN chmod +x /usr/local/bin/entrypoint.sh /opt/smoky_tests/run.py \
 && ln -sf /opt/smoky_tests/run.py /usr/local/bin/smoke-tests \
 && mkdir -p /srv/node/d1 /var/cache/swift /var/log/swift /run/swift /var/spool/rsyslog \
 && chown -R swift:swift /srv/node /var/cache/swift /var/log/swift /run/swift

RUN chown -R syslog:syslog /var/spool/rsyslog || true

EXPOSE 8080
VOLUME ["/srv/node"]

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
