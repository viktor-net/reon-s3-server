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

- Загрузка файла с возвратом `fileId`.
- Скачивание файла, `HEAD` и получение описания.
- Список файлов с фильтрами, сортировкой и страницами.
- Изменение имени, описания и тегов без перезагрузки файла.
- Копирование файла и массовое удаление.
- Явные multipart-сессии: начать, загрузить части, завершить, прервать, посмотреть статус.
- Статистика и информация о хранилище.
- OpenAPI + Scalar UI с примерами.
- Фоновая сверка Postgres и S3, очистка orphan-объектов и зависших multipart upload.
- Health checks `/health/live` и `/health/ready`.

## Реестр эндпоинтов

Клиентской аутентификации нет: сервис рассчитан на доверенный внутренний контур.

| Метод | Путь | Назначение |
|---|---|---|
| `POST` | `/api/v1/files` | Загрузить файл, вернуть `fileId` |
| `GET` | `/api/v1/files` | Список файлов с фильтрами |
| `GET` | `/api/v1/files/{fileId}` | Скачать файл |
| `HEAD` | `/api/v1/files/{fileId}` | Заголовки файла без тела |
| `GET` | `/api/v1/files/{fileId}/metadata` | Получить описание |
| `PATCH` | `/api/v1/files/{fileId}/metadata` | Изменить имя, описание, теги |
| `DELETE` | `/api/v1/files/{fileId}` | Удалить файл |
| `POST` | `/api/v1/files/{fileId}/copy` | Скопировать файл |
| `POST` | `/api/v1/files/bulk-delete` | Удалить несколько файлов |
| `POST` | `/api/v1/uploads` | Начать multipart-загрузку |
| `GET` | `/api/v1/uploads` | Список активных multipart-загрузок |
| `GET` | `/api/v1/uploads/{uploadId}` | Статус загрузки и список частей |
| `PUT` | `/api/v1/uploads/{uploadId}/parts/{partNumber}` | Загрузить часть |
| `POST` | `/api/v1/uploads/{uploadId}/complete` | Завершить загрузку и создать файл |
| `DELETE` | `/api/v1/uploads/{uploadId}` | Прервать загрузку |
| `POST` | `/api/v1/uploads/{uploadId}/abort` | Прервать загрузку (алиас) |
| `GET` | `/api/v1/storage/stats` | Статистика по файлам |
| `GET` | `/api/v1/storage/info` | Бакет, лимиты и настройки |
| `GET` | `/health/live`, `/health/ready` | Проверки живости и готовности |

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
│   │   │   ├── FilesController.cs         # файлы: загрузка, список, скачивание, PATCH, copy
│   │   │   ├── UploadsController.cs       # multipart-сессии
│   │   │   └── StorageController.cs       # статистика и информация о хранилище
│   │   ├── Contracts/
│   │   │   ├── UploadFileRequest.cs       # multipart: файл + JSON-описание
│   │   │   ├── FileMetadataResponse.cs
│   │   │   ├── FilesContracts.cs          # список, PATCH, copy, bulk delete
│   │   │   ├── MultipartContracts.cs      # сессии загрузки и части
│   │   │   └── StorageInfoResponse.cs
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
│   │   │   ├── FileMetadata.cs            # доменная модель и переходы статусов
│   │   │   ├── MultipartUpload.cs         # состояние multipart-сессии
│   │   │   └── MultipartUploadPart.cs     # загруженная часть
│   │   ├── Enums/
│   │   │   ├── FileStatus.cs              # Pending/Ready/Deleting/...
│   │   │   └── MultipartUploadStatus.cs   # Initiated/InProgress/...
│   │   ├── Exceptions/                    # доменные ошибки
│   │   ├── Interfaces/                    # IObjectStorage, IMultipartUploadService, IClock...
│   │   ├── Models/                        # UploadFileCommand, FileListQuery, StorageStats
│   │   ├── Options/
│   │   │   └── FileStorageOptions.cs      # лимиты, таймауты, префикс ключей
│   │   └── Services/
│   │       ├── FileInput.cs               # общая валидация и построение ключа
│   │       ├── FileStorageService.cs      # загрузка, выдача и удаление файлов
│   │       └── MultipartUploadService.cs  # явные multipart-сессии
│   └── Reon.Infrastructure/
│       ├── GlobalUsings.cs                # общие using для инфраструктуры
│       ├── Configuration/
│       │   └── S3Options.cs               # endpoint, креды, бакет
│       ├── Persistence/
│       │   ├── ReonDbContext.cs
│       │   ├── Configurations/
│       │   │   ├── FileMetadataConfiguration.cs
│       │   │   └── MultipartUploadConfiguration.cs
│       │   ├── FileMetadataRepository.cs
│       │   ├── MultipartUploadRepository.cs
│       │   └── Migrations/                # InitialCreate, multipart, индексы списка
│       ├── Reconciliation/
│       │   ├── ReconciliationService.cs
│       │   ├── ReconciliationBackgroundService.cs
│       │   ├── OrphanSweepBackgroundService.cs
│       │   └── MultipartCleanupBackgroundService.cs
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
        ├── MultipartUploadServiceTests.cs
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
| Есть объект в S3, нет строки в Postgres | Orphan. Фоновая очистка удаляет объект, если он старше grace period (по умолчанию 24 часа) и его ключ соответствует формату `files/{fileId}{расширение}`. Чужие объекты не трогаются. |
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

### Список файлов

```bash
curl 'http://localhost:8090/api/v1/files?page=1&pageSize=50&status=Ready&sort=-createdAt'
```

Фильтры:

- `status` — `Pending`, `Ready`, `Deleting`, `Failed`, `Missing`, `Corrupted`.
- `contentType` — точное совпадение.
- `originalName` — поиск подстроки по имени.
- `createdFrom`, `createdTo` — границы по времени создания.
- `sort` — `createdAt`, `-createdAt`, `size`, `-size`, `originalName`, `-originalName`.
- `page`, `pageSize` — страницы, `pageSize` максимум 200.

Ответ:

```json
{
  "items": [ ... ],
  "total": 120,
  "page": 1,
  "pageSize": 50
}
```

### HEAD

```bash
curl -I http://localhost:8090/api/v1/files/{fileId}
```

Возвращает `Content-Type`, `Content-Length`, `ETag` без тела. Если файл не
`Ready`, будет `409`.

### Изменение описания

```bash
curl -X PATCH http://localhost:8090/api/v1/files/{fileId}/metadata \
  -H 'Content-Type: application/json' \
  -d '{"originalName":"new.png","description":"новое описание","tags":{"kind":"avatar"}}'
```

- `null` или отсутствующее поле — оставить как есть.
- `description: ""` — очистить описание.
- `tags: {}` — очистить теги.
- `originalName` меняет имя при скачивании, но **не** переименовывает объект в S3.

### Копирование

```bash
curl -X POST http://localhost:8090/api/v1/files/{fileId}/copy \
  -H 'Content-Type: application/json' \
  -d '{"originalName":"copy.png","description":"копия"}'
```

Создаёт новый `fileId`, копирует объект в S3 и описание в Postgres.

### Массовое удаление

```bash
curl -X POST http://localhost:8090/api/v1/files/bulk-delete \
  -H 'Content-Type: application/json' \
  -d '{"fileIds":["..."],"continueOnError":true}'
```

До 100 уникальных `fileId`. В ответе — результат по каждому файлу
(`Deleted`, `AlreadyDeleted`, `Deleting`, `NotFound`, `Failed`).

### Multipart upload

Для больших файлов клиент может загружать части явно. S3 upload id и
S3-авторизация остаются внутри сервиса; клиент видит только `uploadId`.

1. Начать загрузку:

```bash
curl -X POST http://localhost:8090/api/v1/uploads \
  -H 'Content-Type: application/json' \
  -d '{"originalName":"big.bin","contentType":"application/octet-stream","description":"большой файл"}'
```

Ответ `201 Created`:

```json
{
  "uploadId": "...",
  "fileId": "...",
  "status": "Initiated",
  "partSize": 16777216,
  "minPartSize": 5242880,
  "maxParts": 10000,
  "expiresAt": "2026-11-05T10:00:00+00:00",
  "parts": []
}
```

2. Загрузить часть:

```bash
curl -X PUT http://localhost:8090/api/v1/uploads/{uploadId}/parts/1 \
  -H 'Content-Type: application/octet-stream' \
  --data-binary @part1.bin
```

`Content-Length` обязателен. Все части, кроме последней, должны быть не меньше
`MinMultipartPartSizeBytes` (5 MiB). Части можно загружать в любом порядке, но
номера должны быть последовательными при завершении.

3. Посмотреть статус и части:

```bash
curl http://localhost:8090/api/v1/uploads/{uploadId}
```

4. Завершить:

```bash
curl -X POST http://localhost:8090/api/v1/uploads/{uploadId}/complete
```

Ответ `201 Created` с обычными метаданными файла. Сервис проверяет размер,
считает SHA-256, создаёт запись в Postgres и возвращает `fileId`.

5. Прервать загрузку:

```bash
curl -X DELETE http://localhost:8090/api/v1/uploads/{uploadId}
```

или:

```bash
curl -X POST http://localhost:8090/api/v1/uploads/{uploadId}/abort
```

Оба варианта вызывают `AbortMultipartUpload` в S3. Повторный abort уже
прерванной загрузки идемпотентен.

Незавершённые загрузки живут `FileStorage:MultipartUploadLifetimeDays`
(по умолчанию 7 дней, можно поставить 3–7). Фоновая очистка раз в
`MultipartCleanupIntervalMinutes` абортит истёкшие сессии и S3 multipart upload
старше этого срока, чтобы не утекало место в хранилище.

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
| `409` | Файл не `Ready`, multipart-загрузка уже завершена/прервана или неактивна |
| `410` | Multipart-загрузка истекла |
| `411` | Для части не передан `Content-Length` |
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
| `FileStorage:MultipartPartSizeBytes` | `16777216` | Рекомендуемый размер части (16 MiB) |
| `FileStorage:MinMultipartPartSizeBytes` | `5242880` | Минимум для всех частей, кроме последней |
| `FileStorage:MaxMultipartParts` | `10000` | Максимум частей в одной загрузке |
| `FileStorage:MultipartUploadLifetimeDays` | `7` | Срок жизни незавершённой загрузки |
| `FileStorage:MultipartCleanupIntervalMinutes` | `60` | Интервал очистки зависших загрузок |
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

Таблицы multipart-загрузок:

```
multipart_uploads
  id uuid pk
  file_id uuid
  storage_upload_id varchar(512) unique
  bucket, object_key, original_name, content_type
  description, tags jsonb, owner
  status varchar(32)
  created_at, updated_at, expires_at, completed_at
  failure_reason varchar(1000)

multipart_upload_parts
  upload_id uuid fk → multipart_uploads.id
  part_number int
  etag varchar(128)
  size bigint
  uploaded_at timestamptz
  pk (upload_id, part_number)
```

Индексы:

- `ux_files_bucket_object_key` — уникальность объекта.
- `ix_files_status_updated_at` — выборка `Pending`/`Deleting` для сверки.
- `ix_files_status_deleted_at` — очистка soft-deleted строк, partial index.
- `ix_files_owner` — служебный индекс; для доступа сейчас не используется.
- `ix_files_status_created_at`, `ix_files_created_at` — список и сортировка.
- `ix_files_content_type`, `ix_files_original_name` — фильтры списка.
- `ix_multipart_uploads_status_expires_at` — очистка зависших загрузок.

## S3 и multipart

- Ключ объекта генерируется сервисом: `files/{fileId:N}{расширение}`. Расширение
  берётся из `originalName` и нужно, чтобы Filestash показывал привычный формат
  файла; имя файла целиком в ключ не попадает. Старые объекты, загруженные до
  этого изменения, остаются без расширения в ключе.
- До 64 MiB — обычный `PutObject`, больше — multipart с частью 16 MiB.
- Для больших файлов доступны явные multipart-сессии: `POST /api/v1/uploads`,
  части, complete/abort. S3 upload id наружу не отдаётся.
- При ошибке multipart вызывается `AbortMultipartUpload`.
- Истёкшие multipart-сессии и S3 upload старше
  `MultipartUploadLifetimeDays` (по умолчанию 7 дней) абортятся фоновой очисткой.
  Это защита от утечки места в S3, а не памяти.
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

Unit-тесты покрывают успешную загрузку, ошибку S3, потерю объекта, удаление,
превышение размера и multipart-сессии. Всего 8 тестов.

S3-слой дополнительно проверен вручную против `swift-s3api`: создание бакета,
`PutObject`, `HeadObject` с metadata, `GetObject`, `DeleteObject`,
multipart upload размером 6 MiB и явные multipart-сессии.

## Решения по неоднозначностям

Эти решения зафиксированы, чтобы не обсуждать их повторно:

| Вопрос | Решение |
|---|---|
| Кто источник истины по метаданным | PostgreSQL |
| Кто источник истины по байтам | S3 |
| Кто пишет в S3 | Только этот сервис |
| Аутентификация клиентов | Не реализована; сервис для доверенного внутреннего контура |
| Что такое `version` | Бизнес-версия, сейчас всегда `1`; версия S3-объекта хранится отдельно в `s3_version_id` |
| Базовый путь | `/api/v1/files`, `/api/v1/uploads`, `/api/v1/storage` |
| Формат загрузки | `multipart/form-data` для простой; бинарные части для явного multipart |
| Формат метаданных и ошибок | JSON; ошибки — RFC 9457 ProblemDetails |
| Формат содержимого | Бинарный поток, без base64 |
| Удаление | Soft delete в Postgres + удаление объекта из S3; ответ 204/202 |
| Повторное удаление | Идемпотентно: 204, если уже удалён |
| Перезапись | Нет: каждый upload создаёт новый `fileId` |
| Листинг | Есть: `GET /api/v1/files` с фильтрами и страницами |
| Range, presigned URL, S3 versioning | Нет |
| Максимальный размер | 1 GiB, настраивается |
| Multipart | Автоматический для простой загрузки >64 MiB и явные сессии `/api/v1/uploads` |
| Один инстанс или несколько | Несколько инстансов безопасны: сверка берёт Postgres advisory lock |

## Осознанные ограничения

- Нет перезаписи: каждый upload создаёт новый `fileId`.
- Нет S3 versioning и presigned URL: наружу отдаётся только REST-фасад.
- Нет Range-запросов: файл отдаётся целиком.
- Один бакет на инстанс.
- Нет антивируса и content-scanning.
- Клиентской аутентификации нет: сервис нельзя публиковать в недоверенную сеть
  без внешнего шлюза. Scalar UI тоже открыт.
- Поиск по `originalName` — подстрочный; для больших каталогов нужен trigram
  или отдельный поисковый индекс.
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