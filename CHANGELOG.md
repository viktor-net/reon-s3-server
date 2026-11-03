# Changelog

История изменений репозитория. Формат основан на [Keep a Changelog](https://keepachangelog.com/ru/1.1.0/).

## [Unreleased]

### Добавлено

- Временные ссылки на скачивание через API: `QUERY .../presigned-urls`
  возвращает HMAC-подписанную ссылку `GET /api/admin/download`, которая не
  раскрывает S3 endpoint. Параметры: `Admin:PublicBaseUrl`,
  `Admin:DownloadLinkSecret`.
- Internal-сеть `s3-internal` и изоляция Swift: контейнер `swift` больше не
  публикует порт на хост и доступен только контейнеру API.
- `docker-compose.dev.yml` — отдельный override для отладки API на хосте:
  публикует S3 только на `127.0.0.1:10001`.
- Профиль `dev-tools` для Swagger UI и Filestash: инструменты имеют прямой
  доступ к S3 и не должны работать в production.
- Юнит-тесты на подпись и проверку временных ссылок.
- `GET /api/admin/buckets/{bucket}/versioning` — чтение состояния
  версионирования бакета.
- `PUT /api/admin/buckets/{bucket}/versioning` — включение (`enabled=true`) и
  приостановка (`enabled=false`) версионирования отдельного бакета. Действие
  пишется в аудит как `bucket.versioning.update` и публикует событие
  `BucketChanged`.
- `allow_object_versioning = true` в `[filter:versioned_writes]` конфигурации
  Swift s3api: стенд теперь поддерживает S3-версионирование бакетов.
- Юнит-тесты на чтение, включение, приостановку и недоступность
  версионирования у провайдера.
- `CHANGELOG.md`.

### Исправлено

- `PUT /api/admin/buckets/{bucket}/lifecycle` на провайдере без поддержки
  lifecycle (Swift s3api) отвечал `502 Object storage unavailable` хотя
  хранилище доступно. Теперь ответ `400 MalformedXML` распознаётся как
  отсутствие возможности и возвращается `501` с понятным описанием.

### Изменено

- Временные ссылки больше не являются S3 presigned URL: они ведут на API
  (`/api/admin/download`) и подписываются HMAC. Прямой доступ клиентов к S3
  запрещён и не требуется.
- `swift` в compose больше не публикует `10001`; наружу доступен только API.
- README: добавлено жёсткое требование «S3 доступен только API», обновлены
  адреса стенда, конфигурация и troubleshooting.
- README: описаны эндпоинты версионирования, семантика `Suspended`, требования
  к Swift и ограничения стенда (lifecycle, подсчёт использования, delete-маркеры).
