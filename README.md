# Reon S3 Server

Сервис хранения файлов для внутренних модулей. Это единственный владелец
информации о файлах: описания и жизненный цикл ведутся в PostgreSQL, содержимое
лежит в S3. Остальные модули работают через JSON и бинарный поток и не знают
деталей S3: ни XML, ни подписи запросов, ни параллельной загрузки файла по частям.

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
- **Абстрагирование** — наружу виден контракт; S3, параллельная загрузка файла по частям и служебные детали
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
- Необязательный срок хранения (`retentionSeconds`): просроченный файл отвечает
  `410`, а фоновый сборщик удаляет объект из S3. Срок можно продлить, а удаление
  по сроку — приостановить через `retentionHold`.
- Скачивание файла, `HEAD` и получение описания.
- Список файлов с фильтрами, сортировкой и страницами.
- Изменение имени, описания и тегов без перезагрузки файла.
- Копирование файла и массовое удаление.
- Явные сессии параллельной загрузки файла по частям: начать, загрузить части, завершить, прервать, посмотреть статус.
- Статистика и информация о хранилище.
- Административный фасад `/api/admin`: обзор, бакеты, объекты, версии,
  квоты, lifecycle, анонимный доступ и журнал операций.
- WebSocket-поток административных событий.
- OpenAPI + Scalar UI с примерами.
- Фоновая сверка Postgres и S3, очистка orphan-объектов и зависших сессий параллельной загрузки файла по частям.
- Health checks `/health/live` и `/health/ready`.

## Реестр эндпоинтов

Клиентской аутентификации нет: сервис рассчитан на доверенный внутренний контур.

| Метод | Путь | Назначение |
|---|---|---|
| `POST` | `/api/files` | Загрузить файл, вернуть `fileId` |
| `GET` | `/api/files` | Список файлов с фильтрами |
| `GET` | `/api/files/{fileId}` | Скачать файл |
| `HEAD` | `/api/files/{fileId}` | Заголовки файла без тела |
| `GET` | `/api/files/{fileId}/metadata` | Получить описание |
| `PATCH` | `/api/files/{fileId}/metadata` | Изменить имя, описание, теги |
| `PATCH` | `/api/files/{fileId}/retention` | Изменить срок хранения или `retentionHold` |
| `DELETE` | `/api/files/{fileId}` | Удалить файл |
| `POST` | `/api/files/{fileId}/copy` | Скопировать файл |
| `POST` | `/api/files/bulk-delete` | Удалить несколько файлов |
| `POST` | `/api/uploads` | Начать параллельную загрузку файла по частям |
| `GET` | `/api/uploads` | Список активных параллельных загрузок файла по частям |
| `GET` | `/api/uploads/{uploadId}` | Статус загрузки и список частей |
| `PUT` | `/api/uploads/{uploadId}/parts/{partNumber}` | Загрузить часть |
| `POST` | `/api/uploads/{uploadId}/complete` | Завершить загрузку и создать файл |
| `DELETE` | `/api/uploads/{uploadId}` | Прервать загрузку |
| `POST` | `/api/uploads/{uploadId}/abort` | Прервать загрузку (алиас) |
| `GET` | `/api/storage/stats` | Статистика по файлам |
| `GET` | `/api/storage/info` | Бакет, лимиты и настройки |
| `GET` | `/health/live`, `/health/ready` | Проверки живости и готовности |

## Административный фасад

Клиентский API выше не меняется. Рядом живёт отдельный контур
`/api/admin` — фасад для портала администрирования хранилища. Он собирает
несколько запросов к S3 и Postgres и отдаёт простые JSON-модели.

Принципы:

- старые `/api/files`, `/api/uploads`, `/api/storage` остаются
  фундаментальной функциональностью; фасад их не заменяет и не дублирует
  сценарии жизненного цикла файлов;
- фасад работает со всеми бакетами, клиентский API — с бакетом по умолчанию
  (`S3:BucketName`);
- вызывающая сторона не аутентифицируется, но может передать `X-Reon-Actor`;
  значение попадает в журнал, по умолчанию используется `Admin:DefaultActor`;
- возможности, которых нет у S3-провайдера (например, bucket policy на Swift
  s3api), на запись возвращают `501 Not Implemented` в формате ProblemDetails,
  а на чтение деградируют до пустых значений.

### Чем фасад отличается от базового API

Базовый API — это файловый домен: единица работы `fileId`, метаданные в
Postgres, статусы `Pending → Ready → Deleted`, сверка с S3 и сессии параллельной загрузки файла по частям.
Фасад — это домен хранилища: бакеты, сырые ключи, версии и настройки S3.

| Задача | Базовый API | Фасад |
|---|---|---|
| Идентификация | `fileId` | `bucket` + `key` |
| Единица работы | файл с описанием и жизненным циклом | объект S3, в том числе не учтённый в Postgres |
| Бакеты | один (`S3:BucketName`) | все бакеты, создание и настройки |
| Список | строки `files` с фильтрами и страницами | объекты и префиксы в S3 |
| Загрузка | `Pending → Ready`, SHA-256, параллельная загрузка файла по частям, запись в БД | запись объекта по произвольному ключу |
| Удаление | статусная машина + фоновая сверка | немедленное удаление объекта или версии |
| Специфика | — | квоты, usage, access, lifecycle, presigned URL, аудит, WS |

Фасад не создаёт строки `files` и не участвует в сверке. Базовый API не умеет
то, что нужно админке: список сырых объектов и «папок», версии, presigned URL,
квоты, политики и агрегаты по всем бакетам.

Минимально для экранов прототипа нужны: обзор, список/создание/настройки
бакетов, браузер объектов, загрузка, карточка объекта с версиями и ссылкой,
удаление, usage, access, аудит и WS. Эндпоинты `PUT .../lifecycle`,
`PUT .../tags`, `PUT .../objects/{**key}`, `GET .../usage`,
`POST .../usage/refresh` и `DELETE .../versions/{versionId}` — расширения:
их можно убрать без потери функциональности прототипа.

### Эндпоинты фасада

| Метод | Путь | Назначение |
|---|---|---|
| `GET` | `/api/admin/overview` | Обзор: бакеты, объекты, объём, публичные бакеты, использование |
| `GET` | `/api/admin/buckets` | Список бакетов: `search`, `sort=name\|size\|objects`, страницы |
| `POST` | `/api/admin/buckets` | Создать бакет (версионирование, Object Lock, квота) |
| `GET` | `/api/admin/buckets/{bucket}` | Детали бакета: версии, Object Lock, шифрование, квота, использование |
| `PATCH` | `/api/admin/buckets/{bucket}` | Версионирование и квота (Object Lock неизменяем) |
| `GET` | `/api/admin/buckets/{bucket}/access` | Анонимный доступ и текущая bucket policy |
| `PUT` | `/api/admin/buckets/{bucket}/access` | `private` или `public-read` |
| `GET` | `/api/admin/buckets/{bucket}/lifecycle` | Правила lifecycle |
| `PUT` | `/api/admin/buckets/{bucket}/lifecycle` | Заменить правила lifecycle |
| `GET` | `/api/admin/buckets/{bucket}/usage` | Использование и квота (`refresh=true` — пересчёт) |
| `POST` | `/api/admin/buckets/{bucket}/usage/refresh` | Пересчитать использование и разослать событие |
| `POST` | `/api/admin/buckets/{bucket}/folders` | Создать префикс-маркер папки |
| `GET` | `/api/admin/buckets/{bucket}/objects` | Объекты и префиксы: `prefix`, `delimiter`, `search`, страницы |
| `POST` | `/api/admin/buckets/{bucket}/objects` | Загрузить несколько файлов (`multipart/form-data`), батч откатывается при ошибке |
| `PUT` | `/api/admin/buckets/{bucket}/objects/{**key}` | Загрузить один объект потоком |
| `GET` | `/api/admin/buckets/{bucket}/objects/{**key}` | Скачать объект (`versionId`) |
| `HEAD` | `/api/admin/buckets/{bucket}/objects/{**key}` | Заголовки объекта |
| `DELETE` | `/api/admin/buckets/{bucket}/objects/{**key}` | Удалить объект или версию |
| `GET` | `/api/admin/buckets/{bucket}/objects/{key}/metadata` | Метаданные, теги, число версий |
| `GET` | `/api/admin/buckets/{bucket}/objects/{key}/versions` | История версий |
| `DELETE` | `/api/admin/buckets/{bucket}/objects/{key}/versions/{versionId}` | Удалить конкретную версию |
| `GET` | `/api/admin/buckets/{bucket}/objects/{key}/tags` | Теги объекта |
| `PUT` | `/api/admin/buckets/{bucket}/objects/{key}/tags` | Заменить теги объекта |
| `QUERY` | `/api/admin/buckets/{bucket}/objects/{key}/presigned-urls` | Временная ссылка на скачивание |
| `GET` | `/api/admin/audit` | Журнал операций с фильтрами и страницами |
| `WS` | `/api/admin/events` | WebSocket-поток событий |

### Ключи объектов в URL

Для операций с самим объектом ключ передаётся как путь и может содержать
слэши: `GET /api/admin/buckets/documents/objects/contracts/2026/a.pdf`.

Подресурсы (`metadata`, `versions`, `tags`, `presigned-urls`) используют один
сегмент пути, поэтому ключ нужно URL-кодировать:
`GET /api/admin/buckets/documents/objects/contracts%2F2026%2Fa.pdf/metadata`.

### Соответствие экранам прототипа

| Экран прототипа | Что использовать |
|---|---|
| Обзор | `GET /api/admin/overview` |
| Бакеты | `GET/POST /api/admin/buckets` |
| Объекты | `GET /api/admin/buckets/{bucket}/objects`, `POST .../objects`, `PUT .../objects/{**key}` |
| Карточка объекта | `GET .../objects/{key}/metadata`, `GET .../objects/{key}/versions`, `QUERY .../objects/{key}/presigned-urls` |
| Настройки бакета | `PATCH /api/admin/buckets/{bucket}`, `GET/PUT .../lifecycle`, `GET .../usage` |
| Управление доступом | `GET/PUT /api/admin/buckets/{bucket}/access` |
| Использование и квоты | `GET /api/admin/overview`, `GET .../usage`, `POST .../usage/refresh` |
| Журнал операций | `GET /api/admin/audit` |
| Живые обновления | `WS /api/admin/events` |

`GET .../objects` по умолчанию не возвращает `contentType`: S3-листинг его не
отдаёт. Передайте `includeContentType=true`, чтобы сервис дополнительно сделал
`HEAD` по объектам страницы (не более 1000, с ограничением параллелизма 8).

### Использование и квоты

Использование считается по S3 (`ListObjectsV2`) и кешируется на
`Admin:UsageCacheSeconds` (60 секунд). Параметр `refresh=true` (или
`POST .../usage/refresh`) пересчитывает значение принудительно.

Квота хранится в Postgres (`bucket_settings`) и проверяется перед записью в
обоих контурах: базовая загрузка (`POST /api/files`), завершение параллельной загрузки файла по частям и
операции фасада. Проверка выполняется до появления строки в `files` и до
записи объекта, поэтому отклонённая загрузка не оставляет следов. Это
**best-effort**-ограничение: объекты, записанные в бакет в обход сервиса,
учитываются только после пересчёта, а параллельные записи могут вместе
пересечь квоту. Статус заполнения считает сервис:
`Ok` < 80 %, `Warning` ≥ 80 %, `Critical` ≥ 95 %, `NoQuota` — если квоты нет.

### Журнал операций

Аудит пишется в Postgres (`audit_events`) и доступен через
`GET /api/admin/audit`:

Один и тот же журнал пишут и фасад, и базовый API:

- фасад: `bucket.create`, `bucket.update`, `bucket.access.update`,
  `bucket.lifecycle.update`, `usage.refresh`, `object.upload`, `object.batch`,
  `object.delete`, `object.version.delete`, `object.tags.update`,
  `object.presign`, `folder.create`;
- базовый API: `file.upload`, `file.metadata.update`, `file.copy`, `file.delete`,
  `file.bulk-delete`, `upload.begin`, `upload.complete`, `upload.abort`;
- `result`: `Success` / `Failure`, плюс `details` с деталями операции
  (для `object.batch` в `details.rolledBack` перечислены удалённые ключи);
- `actor` — из заголовка `X-Reon-Actor`, иначе `Admin:DefaultActor`;
- сбой записи журнала логируется и не влияет на результат операции.

### WebSocket-события

`GET /api/admin/events` — upgrade до WebSocket. Фильтры задаются в query:
`?buckets=documents-prod,media-storage&types=object.changed,usage.updated`
(оба параметра необязательны). Протокол односторонний: сервер присылает JSON,
клиент может отправить `{"type":"ping"}` и получить `{"type":"pong"}`.

Конверт:

```json
{
  "type": "object.changed",
  "occurredAt": "2026-10-29T12:24:00+00:00",
  "bucket": "documents-prod",
  "data": { "bucket": "documents-prod", "key": "contracts/a.pdf", "change": "created", "size": 1024 }
}
```

| Тип | Когда приходит |
|---|---|
| `bucket.changed` | Создание/изменение бакета, access, lifecycle |
| `object.changed` | Загрузка, удаление, теги, удаление версии, создание папки |
| `usage.updated` | Пересчёт использования |
| `audit.appended` | Новая запись журнала |
| `heartbeat` | Keepalive каждые `Admin:EventHeartbeatSeconds` секунд |

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
├── Dockerfile                             # образ API для ручной сборки
├── docker-compose.yml                     # Postgres, Swift, Swagger UI, Filestash
├── docker/
│   ├── filestash/config.json              # seed-конфиг Filestash
│   └── swagger/                           # Swagger UI для S3/Swift API
│       ├── nginx.conf                     # прокси /api → swift:8080
│       ├── openapi.yaml                   # контракт S3/Swift (копия из reon-s3-dev)
│       └── swagger-signer.js              # подпись S3-запросов в браузере
├── README.md                              # описание сервиса, API и архитектуры
├── CONTRIBUTIONS.md                       # правила коммитов
├── .config/
│   └── dotnet-tools.json                  # локальный dotnet-ef для миграций
├── src/
│   ├── Reon.Api/
│   │   ├── Program.cs                     # DI, middleware, OpenAPI/Scalar, health checks
│   │   ├── Controllers/
│   │   │   ├── FilesController.cs         # файлы: загрузка, список, скачивание, PATCH, copy
│   │   │   ├── UploadsController.cs       # сессии параллельной загрузки файла по частям
│   │   │   ├── StorageController.cs       # статистика и информация о хранилище
│   │   │   ├── ReonApiControllerBase.cs   # общий X-Reon-Actor и аудит для всех контроллеров
│   │   │   ├── AdminOverviewController.cs # обзор хранилища
│   │   │   ├── AdminBucketsController.cs  # бакеты, квоты, access, lifecycle
│   │   │   ├── AdminObjectsController.cs  # объекты, версии, теги, presigned URL
│   │   │   └── AdminAuditController.cs    # журнал операций
│   │   ├── Contracts/
│   │   │   ├── UploadFileRequest.cs       # multipart/form-data: файл + JSON-описание
│   │   │   ├── FileMetadataResponse.cs
│   │   │   ├── FilesContracts.cs          # список, PATCH, copy, bulk delete
│   │   │   ├── MultipartContracts.cs      # сессии загрузки и части
│   │   │   ├── StorageInfoResponse.cs
│   │   │   ├── AdminContracts.cs          # DTO административного фасада
│   │   │   └── AdminMappings.cs           # доменные модели → DTO
│   │   ├── Errors/
│   │   │   └── CoreExceptionHandler.cs    # доменные ошибки → ProblemDetails
│   │   ├── Events/
│   │   │   ├── AdminEventHub.cs           # подписки и публикация событий
│   │   │   ├── AdminEventsEndpoint.cs     # WebSocket /api/admin/events
│   │   │   └── AdminEventsHeartbeatService.cs
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
│   │   │   ├── MultipartUpload.cs         # состояние сессии параллельной загрузки файла по частям
│   │   │   ├── MultipartUploadPart.cs     # загруженная часть
│   │   │   ├── BucketSettings.cs          # квота и режим доступа бакета
│   │   │   └── AuditEntry.cs              # запись журнала операций
│   │   ├── Enums/
│   │   │   ├── FileStatus.cs              # Pending/Ready/Deleting/...
│   │   │   ├── MultipartUploadStatus.cs   # Initiated/InProgress/...
│   │   │   ├── BucketAccessMode.cs        # Private/PublicRead
│   │   │   └── AuditResult.cs             # Success/Failure
│   │   ├── Exceptions/                    # доменные ошибки
│   │   ├── Interfaces/                    # IObjectStorage, IStorageAdminService, IClock...
│   │   ├── Models/                        # FileListQuery, StorageStats, модели фасада
│   │   ├── Options/
│   │   │   ├── FileStorageOptions.cs      # лимиты, таймауты, префикс ключей
│   │   │   └── AdminOptions.cs            # страницы, кеш usage, presigned URL
│   │   └── Services/
│   │       ├── FileInput.cs               # общая валидация и построение ключа
│   │       ├── FileStorageService.cs      # загрузка, выдача и удаление файлов
│   │       ├── MultipartUploadService.cs  # явные сессии параллельной загрузки файла по частям
│   │       └── StorageAdminService.cs     # сценарии административного фасада
│   └── Reon.Infrastructure/
│       ├── GlobalUsings.cs                # общие using для инфраструктуры
│       ├── Configuration/
│       │   └── S3Options.cs               # endpoint, креды, бакет
│       ├── Persistence/
│       │   ├── ReonDbContext.cs
│       │   ├── Configurations/
│       │   │   ├── FileMetadataConfiguration.cs
│       │   │   ├── MultipartUploadConfiguration.cs
│       │   │   ├── BucketSettingsConfiguration.cs
│       │   │   └── AuditEntryConfiguration.cs
│       │   ├── FileMetadataRepository.cs
│       │   ├── MultipartUploadRepository.cs
│       │   ├── BucketSettingsRepository.cs
│       │   ├── AuditRepository.cs
│       │   └── Migrations/                # InitialCreate, загрузка по частям, admin storage
│       ├── Reconciliation/
│       │   ├── ReconciliationService.cs
│       │   ├── ReconciliationBackgroundService.cs
│       │   ├── OrphanSweepBackgroundService.cs
│       │   └── MultipartCleanupBackgroundService.cs
│       ├── Services/
│       │   └── SystemClock.cs
│       ├── Storage/
│       │   ├── S3ObjectStorage.cs          # Put/Get/Head/Delete и загрузка по частям
│       │   ├── S3ObjectStorageAdmin.cs     # бакеты, версии, lifecycle, policy, presign
│       │   ├── S3BucketInitializer.cs
│       │   └── TemporaryFileFactory.cs
│       └── DependencyInjection.cs          # регистрация инфраструктуры
└── tests/
    └── Reon.UnitTests/
        ├── GlobalUsings.cs                # общие using для тестов
        ├── FileStorageServiceTests.cs
        ├── MultipartUploadServiceTests.cs
        ├── StorageAdminServiceTests.cs
        └── Fakes/                          # тестовые реализации хранилища и репозиториев
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
Pending → Ready → Expired → Deleted
   |         |        ↑
   |         |        └── retention_hold снят или срок продлён → Ready
   |         → Deleting → Deleted
   |         → Missing   (объект пропал из S3)
   |         → Corrupted (размер/хеш не совпали)
   → Failed
```

| Статус | Значение |
|---|---|
| `Pending` | Строка создана, объект ещё не подтверждён |
| `Ready` | Объект загружен, метаданные подтверждены, файл можно отдавать |
| `Expired` | Срок хранения истёк, API отвечает `410`; объект ещё может лежать в S3 до прохода сборщика |
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
| Есть строка `Ready`, `expires_at` наступил | API отвечает `410` сразу, не дожидаясь сборщика; сборщик переводит строку в `Expired`, затем удаляет объект и ставит `Deleted`. |
| Есть строка `Expired`, объект ещё есть | Штатное состояние до прохода сборщика. Сборщик повторяет удаление; строка остаётся владельцем объекта, orphan sweep её не трогает. |
| `retention_hold = true` | Срок может наступить, но файл остаётся доступным и сборщик его не удаляет. Снятие hold возвращает `Expired` в `Ready`, если объект ещё цел. |
| `retention_hold = true`, но объект уже удалён | Файл остаётся `Expired`/`Deleted`; hold не восстанавливает байты. |
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
5. Если S3 упал — строка становится `Failed`, незавершённая параллельная загрузка файла по частям прерывается.
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
curl -X POST http://localhost:8090/api/files \
  -F "file=@hello.txt" \
  -F 'metadata={"originalName":"hello.txt","contentType":"text/plain","description":"greeting","tags":{"kind":"demo"},"retentionSeconds":2592000}'
```

`metadata` — необязательная JSON-строка. Поля: `originalName`, `contentType`,
`description`, `tags`, `retentionSeconds`. Если `originalName`/`contentType` не
переданы, берутся из части `multipart/form-data`.

`retentionSeconds` — относительный срок хранения в секундах. Сервер сам считает
`expiresAt` и проверяет верхнюю границу `FileStorage:MaxRetentionDays`. Если поле
не передано, применяется `FileStorage:DefaultRetentionDays` (`0` — бессрочно).
Абсолютную дату клиент передать не может.

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
  "updatedAt": "2026-10-28T12:00:00+00:00",
  "expiresAt": "2026-11-27T12:00:00+00:00",
  "retentionHold": false
}
```

`expiresAt = null` означает бессрочное хранение. `retentionHold = true` запрещает
удаление по сроку, не меняя `expiresAt`.

### Получение файла

```bash
curl -OJ http://localhost:8090/api/files/{fileId}
```

Ответ: бинарный поток с `Content-Type`, `Content-Disposition`, `Content-Length`
и `ETag`. `Content-Type` берётся из метаданных файла, имя в
`Content-Disposition` — из `originalName`. Если передать в `metadata.originalName`
имя без расширения, расширение не появится: сервер не угадывает формат по
содержимому. Range-запросы не поддерживаются.

Если срок хранения истёк, возвращается `410 Gone` с `code = FILE_EXPIRED`.
Проверка идёт по `expires_at` и `retention_hold` в Postgres, поэтому файл
перестаёт отдаваться сразу, даже если объект ещё не удалён из S3.

### Получение метаданных

```bash
curl http://localhost:8090/api/files/{fileId}/metadata
```

Метаданные доступны для статусов `Pending`, `Ready`, `Expired`, `Deleting`,
`Failed`, `Missing`, `Corrupted`. Для `Deleted` и отсутствующего `fileId` — 404.
Для просроченного файла описание отдаётся со `status = Expired`, чтобы UI мог
показать срок и предложить продление.

### Удаление

```bash
curl -X DELETE http://localhost:8090/api/files/{fileId}
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
curl 'http://localhost:8090/api/files?page=1&pageSize=50&status=Ready&sort=-createdAt'
```

Фильтры:

- `status` — `Pending`, `Ready`, `Expired`, `Deleting`, `Failed`, `Missing`, `Corrupted`.
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
curl -I http://localhost:8090/api/files/{fileId}
```

Возвращает `Content-Type`, `Content-Length`, `ETag` без тела. Если файл не
`Ready`, будет `409`; если срок хранения истёк — `410`.

### Изменение описания

```bash
curl -X PATCH http://localhost:8090/api/files/{fileId}/metadata \
  -H 'Content-Type: application/json' \
  -d '{"originalName":"new.png","description":"новое описание","tags":{"kind":"avatar"}}'
```

- `null` или отсутствующее поле — оставить как есть.
- `description: ""` — очистить описание.
- `tags: {}` — очистить теги.
- `originalName` меняет имя при скачивании, но **не** переименовывает объект в S3.

### Изменение срока хранения

```bash
curl -X PATCH http://localhost:8090/api/files/{fileId}/retention \
  -H 'Content-Type: application/json' \
  -d '{"retentionSeconds":2592000,"retentionHold":false}'
```

- `retentionSeconds` задаёт новый срок от текущего момента и может вернуть
  просроченный файл в `Ready`, если объект ещё не удалён.
- `retentionHold: true` запрещает удаление по сроку, `false` — снимает запрет.
- Оба поля необязательны; переданные значения применяются вместе.
- Для удалённого файла — `404`.

### Копирование

```bash
curl -X POST http://localhost:8090/api/files/{fileId}/copy \
  -H 'Content-Type: application/json' \
  -d '{"originalName":"copy.png","description":"копия","retentionSeconds":2592000}'
```

Создаёт новый `fileId`, копирует объект в S3 и описание в Postgres. Копия
получает собственный срок хранения: `retentionSeconds` из тела или
`FileStorage:DefaultRetentionDays`, а не остаток срока исходного файла.
Копирование просроченного исходника возвращает `410`.

### Массовое удаление

```bash
curl -X POST http://localhost:8090/api/files/bulk-delete \
  -H 'Content-Type: application/json' \
  -d '{"fileIds":["..."],"continueOnError":true}'
```

До 100 уникальных `fileId`. В ответе — результат по каждому файлу
(`Deleted`, `AlreadyDeleted`, `Deleting`, `NotFound`, `Failed`).

### Параллельная загрузка файла по частям

Для больших файлов клиент может загружать части явно. S3 upload id и
S3-авторизация остаются внутри сервиса; клиент видит только `uploadId`.

1. Начать загрузку:

```bash
curl -X POST http://localhost:8090/api/uploads \
  -H 'Content-Type: application/json' \
  -d '{"originalName":"big.bin","contentType":"application/octet-stream","description":"большой файл","retentionSeconds":604800}'
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
  "parts": [],
  "retentionSeconds": 604800
}
```

`expiresAt` в ответе сессии — срок жизни **самой загрузки**, а не будущего
файла. `retentionSeconds` — срок хранения файла; он применяется при завершении,
поэтому длительная загрузка не сокращает срок жизни файла.

2. Загрузить часть:

```bash
curl -X PUT http://localhost:8090/api/uploads/{uploadId}/parts/1 \
  -H 'Content-Type: application/octet-stream' \
  --data-binary @part1.bin
```

`Content-Length` обязателен. Все части, кроме последней, должны быть не меньше
`MinMultipartPartSizeBytes` (5 MiB). Части можно загружать в любом порядке, но
номера должны быть последовательными при завершении.

3. Посмотреть статус и части:

```bash
curl http://localhost:8090/api/uploads/{uploadId}
```

4. Завершить:

```bash
curl -X POST http://localhost:8090/api/uploads/{uploadId}/complete
```

Ответ `201 Created` с обычными метаданными файла. Сервис проверяет размер,
считает SHA-256, создаёт запись в Postgres и возвращает `fileId` и `expiresAt`.
Срок хранения отсчитывается от момента завершения загрузки.

5. Прервать загрузку:

```bash
curl -X DELETE http://localhost:8090/api/uploads/{uploadId}
```

или:

```bash
curl -X POST http://localhost:8090/api/uploads/{uploadId}/abort
```

Оба варианта вызывают `AbortMultipartUpload` в S3. Повторный abort уже
прерванной загрузки идемпотентен.

Незавершённые загрузки живут `FileStorage:MultipartUploadLifetimeDays`
(по умолчанию 7 дней, можно поставить 3–7). Фоновая очистка раз в
`MultipartCleanupIntervalMinutes` абортит истёкшие сессии и S3-загрузки по частям
старше этого срока, чтобы не утекало место в хранилище.

### Ошибки

Все ошибки — `application/problem+json` (RFC 9457):

```json
{
  "type": "https://httpstatuses.com/404",
  "title": "File not found",
  "status": 404,
  "detail": "The requested file does not exist or was deleted.",
  "instance": "/api/files/...",
  "traceId": "0HNA..."
}
```

| Код | Когда |
|---|---|
| `400` | Пустой файл, неверный JSON metadata, превышены лимиты тегов/описания, неверный `retentionSeconds` |
| `404` | Файл не найден или удалён |
| `409` | Файл не `Ready`, параллельная загрузка файла по частям уже завершена/прервана или неактивна |
| `410` | Истёк срок хранения файла (`FILE_EXPIRED`) или сессия параллельной загрузки |
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
| `FileStorage:MultipartThresholdBytes` | `67108864` | Порог параллельной загрузки файла по частям (64 MiB) |
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
| `FileStorage:DefaultRetentionDays` | `0` | Срок хранения новых файлов по умолчанию; `0` — бессрочно |
| `FileStorage:MaxRetentionDays` | `3650` | Верхняя граница `retentionSeconds`; `0` — без ограничения |
| `FileStorage:RetentionSweepIntervalMinutes` | `60` | Как часто сборщик переводит просроченные файлы в `Expired` |
| `FileStorage:RetentionSweepBatchSize` | `1000` | Сколько строк обрабатывать за один проход сборщика |
| `Admin:UsageCacheSeconds` | `60` | Кеш рассчитанного использования бакета |
| `Admin:MaxPageSize` | `200` | Максимальный размер страницы админ-списков |
| `Admin:MaxPresignedUrlLifetimeSeconds` | `604800` | Максимальный срок presigned URL (7 дней) |
| `Admin:MaxSearchObjects` | `5000` | Сколько объектов сканировать при поиске по подстроке |
| `Admin:DefaultActor` | `system` | Actor, если не передан `X-Reon-Actor` |
| `Admin:EventHeartbeatSeconds` | `30` | Интервал heartbeat в WebSocket-потоке |

Dev-значения S3 (`test:tester` / `testing`) лежат в `appsettings.Development.json`
и предназначены только для локального стенда. В production задавайте их через
секреты/переменные окружения.

## Запуск

Compose поднимает инфраструктуру — Postgres, Swift, Swagger UI и Filestash.
API запускается локально из исходников.

### Инфраструктура

Нужен образ Swift из `reon-s3-dev` — соберите его один раз:

```bash
cd ../reon-s3-dev
docker compose build swift
```

Затем:

```bash
cd ../reon-s3-server
docker compose up -d
```

- S3: http://localhost:8080
- Swagger UI для S3/Swift API: http://localhost:8081
- Filestash: http://localhost:8082 (админка `/admin`, `admin` / `admin`)
- Postgres: localhost:5433, БД `reon`, пользователь `reon`

Dockerfile и конфиги Swift остаются в `reon-s3-dev`; здесь только запуск
образа `swift-s3api-aio:2.33` (переопределяется через `SWIFT_IMAGE`).
Внешних сетей нет. Конфиги Swagger UI и Filestash — в `docker/swagger/`
и `docker/filestash/`.

### API

```bash
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

### Демо-данные

Заполнить локальный стенд демо-данными ИС РЕОН (реестровые дела, выписки,
техпланы, фотофиксация, выгрузки для смежных систем):

```bash
bash scripts/seed/seed.sh          # Windows: pwsh -File scripts/seed/seed.ps1
bash scripts/seed/clean.sh         # удалить только демо-данные
```

Скрипт повторяет порядок записи сервиса (`Pending` → объект в S3 → `Ready`)
и не ломает сверку. Подробности и источники по предметке — в
`scripts/seed/README.md`.

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
expires_at timestamptz          -- null = бессрочно
retention_hold boolean          -- запрет на удаление по сроку
expired_at timestamptz          -- когда файл перешёл в Expired
```

`owner` — служебное поле, сейчас всегда `system`; для проверки доступа оно не
используется.

Таблицы сессий параллельной загрузки файла по частям:

```
multipart_uploads
  id uuid pk
  file_id uuid
  storage_upload_id varchar(512) unique
  bucket, object_key, original_name, content_type
  description, tags jsonb, owner
  status varchar(32)
  created_at, updated_at, expires_at, completed_at
  retention_seconds int          -- срок хранения будущего файла; null = бессрочно
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
- `ix_files_retention_due` — выборка `Ready` с наступившим `expires_at`, partial index.
- `ix_files_status_expired_at` — выборка `Expired` для удаления из S3, partial index.
- `ix_multipart_uploads_status_expires_at` — очистка зависших загрузок.

Таблицы и колонки описаны DDL-комментариями (`COMMENT ON TABLE/COLUMN`); они
применяются миграцией `AddDatabaseComments` и видны в `psql` и клиентах БД.
Подробная модель взаимодействия Postgres и S3 — в `docs/database-and-s3.md`.

Административные таблицы:

```
bucket_settings
  bucket varchar(255) pk
  quota_bytes bigint null
  access_mode varchar(32)   -- Private | PublicRead
  created_at, updated_at timestamptz

audit_events
  id uuid pk
  occurred_at timestamptz
  actor varchar(256)
  action varchar(64)
  resource varchar(1200)
  result varchar(16)        -- Success | Failure
  status_code int null
  details jsonb null
```

Квота и режим доступа хранятся в Postgres, потому что стандартный S3 API не
представляет их переносимо. Версионирование, lifecycle, Object Lock и
шифрование читаются из S3 и в базе не дублируются.

## S3 и параллельная загрузка файла по частям

- Ключ объекта генерируется сервисом: `files/{fileId:N}{расширение}`. Расширение
  берётся из `originalName` и нужно, чтобы Filestash показывал привычный формат
  файла; имя файла целиком в ключ не попадает. Старые объекты, загруженные до
  этого изменения, остаются без расширения в ключе.
- До 64 MiB — обычный `PutObject`, больше — параллельная загрузка файла по частям с частью 16 MiB.
- Для больших файлов доступны явные сессии параллельной загрузки файла по частям: `POST /api/uploads`,
  части, complete/abort. S3 upload id наружу не отдаётся.
- При ошибке параллельной загрузки файла по частям вызывается `AbortMultipartUpload`.
- Истёкшие сессии параллельной загрузки файла по частям и S3 upload старше
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
превышение размера, сессии параллельной загрузки файла по частям, срок
хранения, продление и переходы `Expired`/`retention_hold`. Всего 44 теста.

S3-слой дополнительно проверен вручную против `swift-s3api`: создание бакета,
`PutObject`, `HeadObject` с metadata, `GetObject`, `DeleteObject`,
параллельная загрузка файла по частям размером 6 MiB и явные сессии `/api/uploads`.

## Решения по неоднозначностям

Эти решения зафиксированы, чтобы не обсуждать их повторно:

| Вопрос | Решение |
|---|---|
| Кто источник истины по метаданным | PostgreSQL |
| Кто источник истины по байтам | S3 |
| Кто пишет в S3 | Только этот сервис |
| Аутентификация клиентов | Не реализована; сервис для доверенного внутреннего контура |
| Что такое `version` | Бизнес-версия, сейчас всегда `1`; версия S3-объекта хранится отдельно в `s3_version_id` |
| Базовый путь | `/api/files`, `/api/uploads`, `/api/storage` |
| Формат загрузки | `multipart/form-data` для простой; бинарные части для явной параллельной загрузки файла по частям |
| Формат метаданных и ошибок | JSON; ошибки — RFC 9457 ProblemDetails |
| Формат содержимого | Бинарный поток, без base64 |
| Удаление | Soft delete в Postgres + удаление объекта из S3; ответ 204/202 |
| Срок хранения | TTL в Postgres (`expires_at`), `410` на чтении и фоновый сборщик; S3 lifecycle на `files/` не используется |
| Просроченный файл | Статус `Expired`, ответ `410`; объект удаляется сборщиком, строка остаётся для аудита |
| Повторное удаление | Идемпотентно: 204, если уже удалён |
| Перезапись | Нет: каждый upload создаёт новый `fileId` |
| Листинг | Есть: `GET /api/files` с фильтрами и страницами |
| Range, presigned URL, S3 versioning | Нет |
| Максимальный размер | 1 GiB, настраивается |
| Параллельная загрузка | Автоматическая для простой загрузки >64 MiB и явные сессии `/api/uploads` |
| Один инстанс или несколько | Несколько инстансов безопасны: сверка и сборщик retention берут Postgres advisory lock |

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
- Orphan sweep загружает все активные `fileId` (`Pending`/`Ready`/`Expired`) из Postgres в память.
  Для очень больших каталогов замените его на постраничное сравнение или S3 Inventory.
- S3 lifecycle на `files/` не используется: правило не различает бессрочные и
  временные объекты. Срок хранения обеспечивает сервис, а не S3.

## Логи

Serilog пишет в терминал человекочитаемые строки, а в
`logs/reon-YYYYMMDD.ndjson` в корне репозитория — NDJSON, по одному
JSON-объекту на строку. Файлы ротируются по дню, хранятся 14 дней.

В журнал попадают HTTP-запросы с методом, путём, статусом и временем,
обработанные доменные ошибки со стеком исключения, ошибки S3 и фоновых
сервисов.

## Troubleshooting

| Симптом | Что проверить |
|---|---|
| `Could not ensure S3 bucket` | Запущен ли `swift-s3api`, верны ли `S3:ServiceUrl` и креды |
| `502` | Доступность S3, path-style, регион |
| Scalar UI пустой | Открывайте `http://localhost:8090/scalar/api`; OpenAPI — `/openapi/api.json`. Scalar запрашивает схему относительным путём, для этого в приложении оставлены алиасы. |
| `Postgres is not reachable` | `docker compose ps`, строка подключения |
| Порт 5432 занят локальным Postgres | Compose публикует Postgres на `localhost:5433`; поменяйте порт и строку подключения при необходимости |
| Миграции не применились | `Database:AutoMigrate` или `dotnet ef database update` |
| Файл `Missing` | Объект удалён из S3; проверьте бэкап/versioning |
