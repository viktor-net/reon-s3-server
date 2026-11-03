# Changelog

Все заметные изменения проекта. Формат основан на
[Keep a Changelog](https://keepachangelog.com/ru/1.1.0/), версии — по SemVer,
когда в проекте появится нумерация релизов.

## [Unreleased]

### Добавлено

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

- README: описаны эндпоинты версионирования, семантика `Suspended`, требования
  к Swift и ограничения стенда (lifecycle, подсчёт использования, delete-маркеры).
