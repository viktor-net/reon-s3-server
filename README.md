# Reon S3 Server

Сервис хранения файлов для внутренних модулей. Это единственный владелец
информации о файлах: описания и жизненный цикл ведутся в PostgreSQL, содержимое
лежит в S3. Остальные модули работают через JSON и бинарный поток и не знают
деталей S3: ни XML, ни подписи запросов, ни multipart upload.

## Место сервиса в архитектуре

Это **один сервис**, а не набор микросервисов. Он отвечает за домен файлов
целиком: загрузка, выдача, описание, удаление, актуальность описаний и
целостность пары «описание — содержимое».

Границы ответственности:

- Только этот сервис пишет в S3-бакет и удаляет из него объекты.
- Только этот сервис ведёт описания файлов. Другие модули не хранят их копии,
  а сохраняют у себя только `fileId` и при необходимости запрашивают описание.
- Клиенты не получают доступ к S3 и не знают, где физически лежит файл.
- Если другому модулю нужны файлы, он идёт через контракт сервиса, а не напрямую
  в хранилище.

По принципам это сервис-ориентированный подход:

- **Слабая связанность** — потребители зависят только от HTTP-контракта, а не от
  внутреннего устройства сервиса.
- **Повторное использование** — один и тот же сервис используют все модули,
  каждому не нужно заново реализовывать загрузку, удаление и учёт файлов.
- **Абстрагирование** — наружу виден контракт; S3, multipart и служебные детали
  скрыты внутри.
- **Автономность** — сервис сам управляет своим состоянием, статусами и
  восстановлением после сбоев.

При этом это **не классическая СОА** с центральной шиной и общей базой данных.
У сервиса собственная база, а интеграция идёт по контракту. Это ближе к модели
«сервис владеет своими данными», чем к общей БД предприятия.

Актуальность обеспечивается не тем, что все пишут в одну базу, а тем, что
**только сервис меняет состояние файлов**, а расхождения лечатся статусами и
фоновой сверкой.

## Что умеет

- `POST /api/v1/files` — загрузка файла, возвращает `fileId`.
- `GET /api/v1/files/{fileId}` — получение файла бинарным потоком.
- `GET /api/v1/files/{fileId}/metadata` — получение метаданных в JSON.
- `DELETE /api/v1/files/{fileId}` — удаление файла.
- OpenAPI + Scalar UI.
- У методов OpenAPI есть описания и примеры запросов/ответов.
- Фоновая сверка Postgres и S3, очистка orphan-объектов и незавершённых multipart upload.
- Health checks `/health/live` и `/health/ready`.

## Архитектура

```
Client
   ↓
Reon.Api    [controllers, ProblemDetails, Scalar]
   ↓
Reon.Core   [FileStorageService, domain state machine, interfaces]
      → IFileMetadataRepository → PostgreSQL [metadata, source of truth]
      → IObjectStorage          → Swift S3   [bytes,    source of truth]
```

Проекты решения:

| Проект | Ответственность |
|---|---|
| `src/Reon.Api` | HTTP, DTO, OpenAPI, health checks |
| `src/Reon.Core` | домен, статусы, сценарии, интерфейсы, без AWS SDK |
| `src/Reon.Infrastructure` | EF Core + Npgsql, S3, temp-файлы, background workers |
| `tests/Reon.UnitTests` | unit-тесты сценариев и переходов статусов |

### Структура проекта

```text
├── Reon.S3.Server.slnx                    # решение: Api, Core, Infrastructure, тесты
├── Directory.Build.props                  # общие настройки сборки
├── global.json                            # версия .NET SDK
├── Dockerfile                             # сборка и запуск API в контейнере
├── docker-compose.yml                     # PostgreSQL + API, внешняя сеть Swift
├── README.md                              # описание сервиса, API и архитектуры
├── CONTRIBUTIONS.md                       # правила коммитов
├── .config/
│   └── dotnet-tools.json                  # локальный dotnet-ef для миграций
├── src/
│   ├── Reon.Api/
│   │   ├── Program.cs                     # DI, middleware, OpenAPI/Scalar, health checks
│   │   ├── Controllers/
│   │   │   └── FilesController.cs         # четыре HTTP-операции
│   │   ├── Contracts/
│   │   │   ├── UploadFileRequest.cs       # multipart: файл + JSON-описание
│   │   │   └── FileMetadataResponse.cs
│   │   ├── Errors/
│   │   │   └── CoreExceptionHandler.cs    # доменные ошибки → ProblemDetails
│   │   ├── Health/
│   │   │   ├── PostgresHealthCheck.cs
│   │   │   └── S3HealthCheck.cs
│   │   ├── OpenApi/
│   │   │   └── OpenApiExamplesOperationTransformer.cs # описания и примеры методов
│   │   ├── Properties/
│   │   │   └── launchSettings.json        # профиль запуска и автозапуск Scalar
│   │   ├── appsettings.json               # общие настройки без секретов
│   │   ├── appsettings.Development.json   # локальные dev-настройки
│   │   └── GlobalUsings.cs                # общие using для API
│   ├── Reon.Core/
│   │   ├── GlobalUsings.cs                # общие using для домена
│   │   ├── Entities/
│   │   │   └── FileMetadata.cs            # доменная модель и переходы статусов
│   │   ├── Enums/
│   │   │   └── FileStatus.cs              # Pending/Ready/Deleting/...
│   │   ├── Exceptions/                    # доменные ошибки
│   │   ├── Interfaces/                    # IObjectStorage, IFileMetadataRepository, IClock...
│   │   ├── Models/                        # UploadFileCommand, FileContent, DeleteOutcome
│   │   ├── Options/
│   │   │   └── FileStorageOptions.cs      # лимиты, таймауты, префикс ключей
│   │   └── Services/
│   │       └── FileStorageService.cs      # загрузка, выдача и удаление файлов
│   └── Reon.Infrastructure/
│       ├── GlobalUsings.cs                # общие using для инфраструктуры
│       ├── Configuration/
│       │   └── S3Options.cs               # endpoint, креды, бакет
│       ├── Persistence/
│       │   ├── ReonDbContext.cs
│       │   ├── Configurations/
│       │   │   └── FileMetadataConfiguration.cs
│       │   ├── FileMetadataRepository.cs
│       │   └── Migrations/                   # миграция InitialCreate
│       ├── Reconciliation/
│       │   ├── ReconciliationService.cs
│       │   ├── ReconciliationBackgroundService.cs
│       │   └── OrphanSweepBackgroundService.cs
│       ├── Services/
│       │   └── SystemClock.cs
│       ├── Storage/
│       │   ├── S3ObjectStorage.cs          # Put/Get/Head/Delete/multipart
│       │   ├── S3BucketInitializer.cs
│       │   └── TemporaryFileFactory.cs
│       └── DependencyInjection.cs          # регистрация инфраструктуры
└── tests/
    └── Reon.UnitTests/
        ├── GlobalUsings.cs                # общие using для тестов
        ├── FileStorageServiceTests.cs
        └── Fakes/                          # тестовые реализации хранилища и репозитория
```

## Источник истины и расхождения

Это главное проектное решение, зафиксированное здесь, чтобы не возвращаться к нему.

**Postgres — единственный источник истины по метаданным и жизненному циклу файла.**
Это внутренняя база сервиса, а не общая база нескольких модулей.

**S3 — единственный источник истины по содержимому.**

**Сервис — единственный владелец информации о файлах и единственный писатель в S3-бакет.**
Другие модули не получают S3-доступ, не пишут в бакет и не хранят копии описаний:
у себя они держат только `fileId` и запрашивают описание через контракт сервиса.
Если другой модуль начнёт писать в бакет или вести свою копию описаний, гарантий
актуальности больше нет.

Для API «файл существует» = есть строка в Postgres со статусом `Ready`.
Без неё файл не отдаётся, даже если объект в S3 есть.

### Статусы

```
Pending → Ready → Deleting → Deleted
   |         |
   |         → Missing   (объект пропал из S3)
   |         → Corrupted (размер/хеш не совпали)
   → Failed
```

| Статус | Значение |
|---|---|
| `Pending` | Строка создана, объект ещё не подтверждён |
| `Ready` | Объект загружен, метаданные подтверждены, файл можно отдавать |
| `Deleting` | Удаление из S3 начато или повторяется |
| `Deleted` | Объект удалён, строка оставлена для аудита |
| `Failed` | Загрузка не удалась, объект удалён или не создан |
| `Missing` | Строка есть, объекта в S3 нет |
| `Corrupted` | Объект есть, но размер/хеш не совпадают с метаданными |

### Сценарии расхождения

| Ситуация | Что делает сервис |
|---|---|
| Есть объект в S3, нет строки в Postgres | Orphan. Фоновая очистка удаляет объект, если он старше grace period (по умолчанию 24 часа) и его ключ соответствует формату `files/{fileId}`. Чужие объекты не трогаются. |
| Есть строка `Ready`, объекта в S3 нет | Файл помечается `Missing`, API отвечает 404, в лог пишется ошибка. Байты не восстанавливаются: это возможно только из бэкапа или S3 versioning. |
| Есть строка `Pending`, объект в S3 есть | Сверка проверяет `reon-file-id`, размер и `reon-sha256`. Совпало — `Ready`; не совпало — объект удаляется, строка становится `Failed`. |
| Есть строка `Pending`, объекта в S3 нет | Строка становится `Failed`. |
| Есть строка `Deleting`, объект ещё есть | Фоновая сверка повторяет удаление. |
| Есть строка `Failed`/`Corrupted`, объект ещё есть | Orphan sweep удаляет объект после grace period: такая строка не считается владельцем. |
| Есть строка `Deleted`, объект ещё есть | Orphan sweep удаляет объект после grace period: удалённая строка больше не считается владельцем. |
| `Ready`, размер/хеш не совпали | Пометка `Corrupted`, файл не отдаётся, ошибка в лог. |
| Postgres недоступен | API отвечает 503/500, S3-операции не начинаются. |
| S3 недоступен | API отвечает 502, статус в Postgres не меняется; операцию можно повторить. |

### Почему не наоборот

Если сделать S3 источником истины, клиентам придётся знать S3 API и подпись
запросов, а бизнес-поля (теги, описание, аудит) хранить будет негде. Кроме того,
Swift s3api не поддерживает весь S3-функционал, а LIST в распределённых
хранилищах не является транзакционным.

### Порядок записи

1. Файл сохраняется во временный файл, считаются размер и SHA-256.
2. В Postgres создаётся строка `Pending`.
3. Объект загружается в S3 с metadata `reon-file-id` и `reon-sha256`.
4. Строка переводится в `Ready`, сохраняются ETag и версия объекта.
5. Если S3 упал — строка становится `Failed`, незавершённый multipart абортится.
6. Если S3 загрузился, а финальный `SaveChanges` упал — сервис пытается удалить
   объект; если не получилось, строка останется `Pending` и сверка её разберёт.

## API

Базовый URL в dev: `http://localhost:8090`.

### Доступ

Клиентской аутентификации нет: сервис рассчитан на доверенный внутренний
контур. Запросы выполняются без заголовков авторизации. Если сервис нужно
открыть в недоверенную сеть, перед ним ставится внешний шлюз с проверкой
доступа.

### Загрузка

```bash
curl -X POST http://localhost:8090/api/v1/files \
  -F "file=@hello.txt" \
  -F 'metadata={"originalName":"hello.txt","contentType":"text/plain","description":"greeting","tags":{"kind":"demo"}}'
```

`metadata` — необязательная JSON-строка. Поля: `originalName`, `contentType`,
`description`, `tags`. Если `originalName`/`contentType` не переданы, берутся из
multipart-части.

Ответ `201 Created`:

```json
{
  "fileId": "019a7e4c-7f0e-7c3a-9a2b-5f5f9f9f9f9f",
  "originalName": "hello.txt",
  "contentType": "text/plain",
  "size": 6,
  "sha256": "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
  "eTag": "\"abc\"",
  "owner": "system",
  "description": "greeting",
  "tags": { "kind": "demo" },
  "status": "Ready",
  "version": 1,
  "failureReason": null,
  "createdAt": "2026-10-28T12:00:00+00:00",
  "completedAt": "2026-10-28T12:00:00+00:00",
  "updatedAt": "2026-10-28T12:00:00+00:00"
}
```

### Получение файла

```bash
curl -OJ http://localhost:8090/api/v1/files/{fileId}
```

Ответ: бинарный поток с `Content-Type`, `Content-Disposition`, `Content-Length`
и `ETag`. `Content-Type` берётся из метаданных файла, имя в
`Content-Disposition` — из `originalName`. Если передать в `metadata.originalName`
имя без расширения, расширение не появится: сервер не угадывает формат по
содержимому. Range-запросы не поддерживаются.

### Получение метаданных

```bash
curl http://localhost:8090/api/v1/files/{fileId}/metadata
```

Метаданные доступны для статусов `Pending`, `Ready`, `Deleting`, `Failed`,
`Missing`, `Corrupted`. Для `Deleted` и отсутствующего `fileId` — 404.

### Удаление

```bash
curl -X DELETE http://localhost:8090/api/v1/files/{fileId}
```

- `204 No Content` — объект удалён, строка переведена в `Deleted`.
- `202 Accepted` — удаление уже выполняется, повторный запрос идемпотентен.
- `204` при повторном удалении уже удалённого файла.
- `404` — файла нет.
- `502` — S3 недоступен; строка останется `Deleting`, фоновая сверка повторит.

Строка в Postgres не удаляется сразу: это soft delete для аудита. Через
`FileStorage:DeletedRetentionDays` (по умолчанию 30 дней) её удаляет background
worker. `0` — хранить бессрочно.

### Ошибки

Все ошибки — `application/problem+json` (RFC 9457):

```json
{
  "type": "https://httpstatuses.com/404",
  "title": "File not found",
  "status": 404,
  "detail": "The requested file does not exist or was deleted.",
  "instance": "/api/v1/files/...",
  "traceId": "0HNA..."
}
```

| Код | Когда |
|---|---|
| `400` | Пустой файл, неверный JSON metadata, превышены лимиты тегов/описания |
| `404` | Файл не найден или удалён |
| `409` | Файл ещё не `Ready` или уже недоступен |
| `413` | Файл больше `FileStorage:MaxFileSizeBytes` |
| `502` | S3 недоступен |
| `503` | Postgres/сервис недоступен |

## Конфигурация

Настройки читаются из `appsettings*.json` и переменных окружения.
Для вложенных ключей используется `__`, например `S3__AccessKey`.

| Ключ | По умолчанию | Описание |
|---|---|---|
| `ConnectionStrings:Postgres` | — | Строка подключения к Postgres |
| `Database:AutoMigrate` | `false` | Применять миграции при старте |
| `S3:ServiceUrl` | `http://localhost:8080` | S3 endpoint |
| `S3:AccessKey` | — | Access key |
| `S3:SecretKey` | — | Secret key |
| `S3:Region` | `us-east-1` | Регион |
| `S3:BucketName` | `reon-files` | Бакет для файлов |
| `S3:ForcePathStyle` | `true` | Обязательно для Swift/MinIO |
| `S3:EnsureBucket` | `true` | Создать бакет при старте |
| `FileStorage:DefaultOwner` | `system` | Служебное значение поля `owner` |
| `FileStorage:MaxFileSizeBytes` | `1073741824` | Максимальный размер файла (1 GiB) |
| `FileStorage:MultipartThresholdBytes` | `67108864` | Порог multipart (64 MiB) |
| `FileStorage:MultipartPartSizeBytes` | `16777216` | Размер части (16 MiB, минимум 5 MiB) |
| `FileStorage:KeyPrefix` | `files/` | Префикс ключей |
| `FileStorage:PendingTimeoutMinutes` | `60` | Когда сверка начинает разбирать `Pending` |
| `FileStorage:DeletingRetryMinutes` | `5` | Интервал повтора удаления |
| `FileStorage:ReconciliationIntervalMinutes` | `5` | Интервал сверки |
| `FileStorage:OrphanSweepEnabled` | `true` | Включить очистку orphan-объектов |
| `FileStorage:OrphanSweepIntervalMinutes` | `360` | Интервал очистки orphan |
| `FileStorage:OrphanGracePeriodHours` | `24` | Grace period для orphan |
| `FileStorage:DeletedRetentionDays` | `30` | Сколько хранить soft-deleted строки |

Dev-значения S3 (`test:tester` / `testing`) лежат в `appsettings.Development.json`
и предназначены только для локального стенда. В production задавайте их через
секреты/переменные окружения.

## Запуск

### Вариант 1: Docker Compose

Нужен уже запущенный Swift из `reon-s3-dev`:

```bash
cd ../reon-s3-dev
docker compose up -d swift
```

Затем:

```bash
cd ../reon-s3-server
docker compose up -d --build
```

- API: http://localhost:8090
- Scalar UI: http://localhost:8090/scalar/v1
- OpenAPI: http://localhost:8090/openapi/v1.json
- Postgres: localhost:5433, БД `reon`, пользователь `reon`

Compose использует внешнюю сеть `openstack-swift-s3api_default` и обращается к
Swift по имени `swift:8080`. Если имя сети в вашем окружении другое, поправьте
`networks.swift.name` в `docker-compose.yml`.

### Вариант 2: локально

```bash
docker compose up -d postgres
dotnet run --project src/Reon.Api
```

API слушает `http://localhost:8090`, S3 — `http://localhost:8080`.

### Миграции

```bash
dotnet tool restore
dotnet ef database update \
  --project src/Reon.Infrastructure \
  --startup-project src/Reon.Api
```

В Development `Database:AutoMigrate=true`, поэтому при обычном запуске миграции
применяются автоматически. В production лучше выполнять их отдельным шагом.

## База данных

Таблица `files` (snake_case):

```
id uuid pk
bucket varchar(255)
object_key varchar(1024) unique with bucket
original_name varchar(512)
content_type varchar(255)
size bigint
sha256 char(64)
etag varchar(128)
s3_version_id varchar(256)
owner varchar(256)
description varchar(2000)
tags jsonb
status varchar(32)
version int default 1
failure_reason varchar(1000)
created_at timestamptz
updated_at timestamptz
completed_at timestamptz
deleted_at timestamptz
```

`owner` — служебное поле, сейчас всегда `system`; для проверки доступа оно не
используется.

Индексы:

- `ux_files_bucket_object_key` — уникальность объекта.
- `ix_files_status_updated_at` — выборка `Pending`/`Deleting` для сверки.
- `ix_files_status_deleted_at` — очистка soft-deleted строк, partial index.
- `ix_files_owner` — служебный индекс; для доступа сейчас не используется.

## S3 и multipart

- Ключ объекта генерируется сервисом: `files/{fileId:N}`. Имя файла и другие
  пользовательские данные никогда не попадают в ключ.
- До 64 MiB — обычный `PutObject`, больше — multipart с частью 16 MiB.
- При ошибке multipart вызывается `AbortMultipartUpload`.
- Дополнительно при каждой фоновой сверке сервис абортит незавершённые upload
  старше часа. Это защита от утечки места в S3, а не памяти.
- Сервис проверяет размер и SHA-256 после загрузки через metadata объекта.
- Бакет создаётся при старте, если `S3:EnsureBucket=true`.

## Безопасность

- Клиентской аутентификации нет: сервис рассчитан на доверенный внутренний контур.
- S3-креды хранятся только на сервере и никогда не возвращаются клиенту.
- Ключ объекта генерируется сервисом — path traversal через имя файла невозможен.
- Имя файла очищается от control-символов и ограничивается по длине.
- Content-Type проверяется на CR/LF и длину.
- В production: TLS на reverse proxy или HTTPS в `S3:ServiceUrl`, отдельные S3-креды
  с доступом только к `reon-files`.

## Тесты

```bash
dotnet test
```

Unit-тесты покрывают успешную загрузку, ошибку S3, потерю объекта, удаление
и превышение размера.

S3-слой дополнительно проверен вручную против `swift-s3api`: создание бакета,
`PutObject`, `HeadObject` с metadata, `GetObject`, `DeleteObject` и multipart
upload размером 6 MiB.

## Решения по неоднозначностям

Эти решения зафиксированы, чтобы не обсуждать их повторно:

| Вопрос | Решение |
|---|---|
| Кто источник истины по метаданным | PostgreSQL |
| Кто источник истины по байтам | S3 |
| Кто пишет в S3 | Только этот сервис |
| Аутентификация клиентов | Не реализована; сервис для доверенного внутреннего контура |
| Что такое `version` | Бизнес-версия, сейчас всегда `1`; версия S3-объекта хранится отдельно в `s3_version_id` |
| Базовый путь | `/api/v1/files` |
| Формат загрузки | `multipart/form-data`: часть `file` + необязательная JSON-строка `metadata` |
| Формат метаданных и ошибок | JSON; ошибки — RFC 9457 ProblemDetails |
| Формат содержимого | Бинарный поток, без base64 |
| Удаление | Soft delete в Postgres + удаление объекта из S3; ответ 204/202 |
| Повторное удаление | Идемпотентно: 204, если уже удалён |
| Перезапись | Нет: каждый upload создаёт новый `fileId` |
| Листинг | Нет: только четыре операции из требований |
| Range, presigned URL, S3 versioning | Нет |
| Максимальный размер | 1 GiB, настраивается |
| Multipart | Скрыт внутри `IObjectStorage`; порог 64 MiB, часть 16 MiB |
| Один инстанс или несколько | Несколько инстансов безопасны: сверка берёт Postgres advisory lock |

## Осознанные ограничения

- Нет листинга файлов: в требованиях только четыре операции.
- Нет перезаписи: каждый upload создаёт новый `fileId`.
- Нет S3 versioning и presigned URL: наружу отдаётся только REST-фасад.
- Нет Range-запросов: файл отдаётся целиком.
- Один бакет на инстанс.
- Нет антивируса и content-scanning.
- Клиентской аутентификации нет: сервис нельзя публиковать в недоверенную сеть
  без внешнего шлюза. Scalar UI тоже открыт.
- Если объект физически потерян, сервис фиксирует `Missing`, но не восстанавливает
  байты. Для восстановления нужен backup или S3 versioning.
- Orphan sweep загружает все активные `fileId` (`Pending`/`Ready`) из Postgres в память.
  Для очень больших каталогов замените его на постраничное сравнение или S3 Inventory.

## Troubleshooting

| Симптом | Что проверить |
|---|---|
| `Could not ensure S3 bucket` | Запущен ли `swift-s3api`, верны ли `S3:ServiceUrl` и креды |
| `502` | Доступность S3, path-style, регион |
| Scalar UI пустой | Открывайте `http://localhost:8090/scalar/v1`; OpenAPI — `/openapi/v1.json`. Scalar запрашивает схему относительным путём, для этого в приложении оставлены алиасы. |
| `Postgres is not reachable` | `docker compose ps`, строка подключения |
| Порт 5432 занят локальным Postgres | Compose публикует Postgres на `localhost:5433`; поменяйте порт и строку подключения при необходимости |
| Миграции не применились | `Database:AutoMigrate` или `dotnet ef database update` |
| Файл `Missing` | Объект удалён из S3; проверьте бэкап/versioning |