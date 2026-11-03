# Дерево каталогов, пути и теги

Документ разбирает текущее состояние сервиса и предлагает целевую модель
хранения файлов в иерархии. Короткий вывод: **древовидная структура уже есть,
но только в админском фасаде и только как эмуляция через S3-префиксы. В
основном файловом API (`/api/files`) её нет — там плоский список с `fileId`.**
Для бизнес-сценария «каталоги, вложенные каталоги, путь и теги» нужно добавить
логическое дерево в Postgres, оставив S3 хранилищем непрозрачных байтов.

## 1. Что есть сейчас

### 1.1 Два контура

| Контур | Идентификация | Хранение структуры | Для чего |
|---|---|---|---|
| `/api/files`, `/api/uploads`, `/api/storage` | `fileId` | нет, плоская таблица `files` | бизнес-файлы, жизненный цикл, retention, сверка |
| `/api/admin/buckets/{bucket}/objects` | `bucket` + сырой `key` | S3-префиксы и `delimiter=/` | администрирование хранилища, версии, сырые объекты |

Это важно: «дерево» в админке и «файлы» в основном API — разные домены.
Сейчас они не связаны: объект, загруженный через `/api/files`, не появляется
в виде бизнес-записи при загрузке через фасад, и наоборот.

### 1.2 Основной файловый API — плоский

- `files` — одна строка на файл, без `folder_id` и без пути.
- Ключ объекта детерминирован и плоский:
  `files/{fileId:N}{ext}` (`FileStorageService.UploadAsync`,
  `FileMetadataConfiguration`).
- `original_name` при загрузке нормализуется через `Path.GetFileName`:
  если клиент пришлёт `contracts/2026/a.pdf`, сохранится только `a.pdf`.
  То есть передать каталог через имя файла нельзя.
- `GET /api/files` умеет фильтры `status`, `contentType`, `originalName`
  (подстрока), `createdFrom`, `createdTo` и сортировку. Фильтров по каталогу,
  тегам, владельцу или сроку нет.
- `GET /api/storage/stats` группирует только по статусу и `contentType`.
- Теги — `jsonb` в `files.tags`, без GIN-индекса. Изменить их можно через
  `PATCH /api/files/{fileId}/metadata`.
- Скачивание и метаданные — только по `fileId`.

### 1.3 Админский фасад — «дерево» через префиксы

- `GET /api/admin/buckets/{bucket}/objects?prefix=...&delimiter=/` возвращает
  `prefixes` (общие префиксы) и `items` (объекты). Это виртуальные папки S3.
- `POST /api/admin/buckets/{bucket}/folders` создаёт пустой объект-маркер
  `prefix/name/`, чтобы папка была видна в листинге.
- Вложенность работает: `prefix=contracts/`, `name=2026` создаёт
  `contracts/2026/`.
- Объект можно скачать/удалить по полному ключу:
  `GET /api/admin/buckets/documents/objects/contracts/2026/a.pdf`.
- Подресурсы (`metadata`, `versions`, `tags`, `presigned-urls`) принимают ключ
  одним сегментом, поэтому ключ нужно URL-кодировать.
- Поиск по имени — не S3-поиск, а обход до `Admin:MaxSearchObjects` объектов
  с фильтрацией в памяти.

Во фронтенде (`reon3_admin_panel`) это уже используется:
`ObjectBrowser` ходит в `listObjects` с `prefix`, `delimiter: '/'` и умеет
заходить в префикс, подниматься на уровень выше, создавать папку и загружать
файлы в текущий префикс.

Важно: в S3 **нет каталогов**. «Папка» — это общий префикс ключей, который
`ListObjectsV2` вычисляет при `delimiter=/`. Если в бакете есть ключи
`contracts/2026/a.pdf` и `contracts/README.txt`, то листинг с `prefix=""` и
`delimiter="/"` вернёт `contracts/` в `CommonPrefixes` **даже если объекта-
маркера `contracts/` не существует**. Маркер `contracts/` нужен только для
того, чтобы пустая папка была видна в листинге. Поэтому удаление маркера
непустой папки ничего не ломает: папка останется, пока под префиксом есть
хотя бы один объект. Если удалить все объекты, исчезнет и папка — либо
останется только пустой маркер, если его не удалили.

Текущий `StorageAdminService.ListObjectsAsync` дополнительно скрывает
маркер самой папки при листинге её содержимого, чтобы нулевой объект не
выглядел как файл.

### 1.4 Теги в S3 и теги в Postgres — это разные теги

| Где | Что | Ограничения | Поиск |
|---|---|---|---|
| S3 object tags | `GET/PUT .../objects/{key}/tags` | до 10 тегов на объект, ключ/значение ограничены S3 | `ListObjectsV2` теги не возвращает; фильтровать нельзя без `GetObjectTagging` по каждому объекту |
| Postgres `files.tags` | `PATCH /api/files/{fileId}/metadata` | до 50 тегов, ключ 64, значение 256 (`FileStorageOptions`) | можно индексировать `jsonb` и фильтровать |

Сейчас они не синхронизируются. Если пользователь поставил тег в карточке
S3-объекта, он не появится в фильтрах основного API, и наоборот.

## 2. Почему S3-префиксы не годятся как основная бизнес-структура

S3-префиксы — нормальный способ навигации по сырому хранилищу, но плохая
основа для каталога файлов:

1. **Переименование и перемещение папки** — это копирование и удаление всех
   объектов префикса. Нет атомарности, дорого, ломает версии и audit trail.
2. **`object_key` — идентичность объекта.** Если ключ начинает зависеть от
   пути, перемещение меняет идентичность, а `files.object_key` и сверка
   начинают расходиться.
3. **Нет транзакций.** Нельзя атомарно переименовать папку и обновить
   метаданные всех файлов.
4. **Теги S3 не индексируются для листинга.** Группировка по тегам через S3
   превращается в N вызовов `GetObjectTagging`.
5. **Ограничения S3.** 10 тегов, ограничения на символы ключа, отсутствие
   реальных каталогов.
6. **Опасность для retention и сверки.** Сервис уже владеет жизненным циклом
   строк `files`; вторая структура в ключах создаёт два источника истины.

Поэтому целевая модель:

> **Postgres — источник истины для логического дерева, путей и тегов.
> S3 — хранилище байтов с плоскими непрозрачными ключами.**

S3-ключ остаётся `files/{fileId}{ext}`. Перемещение файла между каталогами —
это `UPDATE files SET folder_id = ...`, без единого вызова S3. Переименование
каталога — обновление `folders.path` у поддерева, тоже без S3.

## 3. Модель данных

### 3.1 Таблица `folders`

Adjacency list + materialized path. Adjacency list даёт простую навигацию
«дети родителя», materialized path — быстрые запросы поддерева и проверку
циклов при перемещении.

```sql
CREATE TABLE folders (
    id              uuid PRIMARY KEY,
    bucket          varchar(255) NOT NULL,
    parent_id       uuid NULL REFERENCES folders(id) ON DELETE RESTRICT,
    name            varchar(255) NOT NULL,
    path            varchar(1024) NOT NULL,          -- "contracts/2026", без ведущего и хвостового "/"
    path_normalized varchar(1024)
        GENERATED ALWAYS AS (lower(path)) STORED,
    depth           int NOT NULL,
    created_at      timestamptz NOT NULL,
    updated_at      timestamptz NOT NULL,
    created_by      varchar(256) NOT NULL,
    CONSTRAINT ck_folders_name
        CHECK (name <> '' AND name !~ '[/\\]' AND name NOT IN ('.', '..')),
    CONSTRAINT ck_folders_depth CHECK (depth BETWEEN 0 AND 32)
);

CREATE UNIQUE INDEX ux_folders_bucket_path
    ON folders (bucket, path_normalized);

CREATE INDEX ix_folders_bucket_parent_name
    ON folders (bucket, parent_id, name);
```

Почему так:

- `bucket` — дерево принадлежит бакету. Для основного API это бакет по
  умолчанию (`S3:BucketName`); для будущего мультибакетного каталога поле уже
  есть.
- `path` — материализованный путь. Уникальный индекс по `bucket +
  lower(path)` решает и конфликт имён, и уникальность корневых папок.
  Отдельный `COALESCE(parent_id, ...)` не нужен.
- `parent_id` с `ON DELETE RESTRICT` — нельзя случайно удалить папку с
  содержимым на уровне БД.
- `depth` — быстрая проверка лимита вложенности и сортировка.
- `path_normalized` — регистронезависимая уникальность и поиск. Если generated
  column в вашей версии Postgres/EF неудобен, замените на expression index
  `lower(path)`.

### 3.2 Изменения `files`

```sql
ALTER TABLE files
    ADD COLUMN folder_id uuid NULL REFERENCES folders(id) ON DELETE RESTRICT;

CREATE INDEX ix_files_bucket_folder_status_created
    ON files (bucket, folder_id, status, created_at DESC);

-- Теги: containment-запросы вида tags @> '{"kind":"contract"}'
CREATE INDEX ix_files_tags_gin
    ON files USING gin (tags jsonb_path_ops);

-- Подстрочный поиск по имени; требует расширения pg_trgm.
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE INDEX ix_files_original_name_trgm
    ON files USING gin (original_name gin_trgm_ops);
```

`folder_id = NULL` — корень. Существующие файлы автоматически оказываются в
корне, миграция данных не нужна.

Если `pg_trgm` недоступен (нет прав на extension), поиск остаётся `ILIKE`, но
для больших каталогов появится seq scan. Альтернатива — `tsvector` +
`GIN`, но для подстроки `pg_trgm` обычно удобнее.

### 3.3 Полный путь файла

Полный логический путь не хранится в `files`, а вычисляется:

```text
folder.path + "/" + files.original_name
```

Пример: `contracts/2026/a.pdf`. Это позволяет:

- не дублировать путь в каждой строке;
- переименовывать папку одним обновлением `folders.path` поддерева;
- разрешать путь через JOIN `files` ↔ `folders`.

Если понадобится сортировка/поиск по полному пути на миллионах файлов, можно
добавить денормализованный `files.folder_path` или generated column, но
начинать с JOIN проще.

### 3.4 Правила путей и имён

- Путь канонический: без ведущего `/`, без `//`, без `.` и `..`, без `\`,
  без управляющих символов, без пробелов в начале/конце сегмента.
- Сегмент — до 255 символов; полный путь — до 1024; глубина — до 32.
- Имена папок уникальны в рамках родителя без учёта регистра.
- Имена файлов: рекомендуется уникальность в рамках папки без учёта регистра,
  если нужен доступ по «классическому пути». Иначе путь неоднозначен.
  Для существующих данных с дублями:
  - `GET /api/files/resolve?path=...` возвращает `409 AMBIGUOUS_PATH` и список
    кандидатов;
  - либо `latest=true` — вернуть самый новый не удалённый файл.
- При загрузке/копировании конфликт имени решается политикой:
  `reject` (по умолчанию, `409`), `rename` (`a (1).pdf`), `keep` (разрешить
  дубль, путь станет неоднозначным).
- Зарезервированные имена (`CON`, `PRN`, `AUX`, `NUL`, `COM1`…) стоит
  отклонять, если предполагаются Windows-клиенты.

### 3.5 Теги

Оставляем `files.tags jsonb` — это уже реализовано и хорошо подходит для
произвольных пар «ключ-значение». Добавляем:

1. GIN-индекс `jsonb_path_ops` для `tags @> '{"key":"value"}'`.
2. Эндпоинт фасетов `GET /api/files/tags`: `jsonb_each_text(tags)` +
   `GROUP BY key, value` с количеством файлов и байтов.
3. Фильтры `tag=key:value` (повторяемый, несколько = AND) и `tagKey=key`
   (ключ существует). Для OR — отдельный `tagAny`.
4. Ограничения на количество/длину уже есть в `FileStorageOptions`.

Нормализованная таблица `file_tags(file_id, key, value)` нужна только если
появятся:
- управляемый справочник тегов и переименование ключа во всех файлах;
- права на теги;
- очень большие фасеты, где jsonb-агрегация станет узким местом.

До этого `jsonb` + GIN — более простой и достаточный вариант.

### 3.6 S3-теги

Канонические бизнес-теги — в Postgres. S3-теги можно:

- не использовать вовсе (рекомендуется для MVP);
- либо зеркалировать туда best-effort при загрузке/обновлении, если объекты
  читаются внешними S3-инструментами. Тогда нужно помнить про лимит 10 тегов
  и не считать S3-теги источником истины.

Для сырых объектов, загруженных через админский фасад, S3-теги остаются
единственными — это нормально, это другой контур.

### 3.7 Multipart

В `multipart_uploads` добавить `folder_id` (или `folder_path` на время
сессии), чтобы завершённая загрузка создавала файл сразу в нужной папке:

```sql
ALTER TABLE multipart_uploads
    ADD COLUMN folder_id uuid NULL REFERENCES folders(id) ON DELETE RESTRICT;
```

Если папку удалили за время загрузки, завершение возвращает `409`.

## 4. API

### 4.1 Принципы

- `fileId` остаётся каноническим идентификатором. Путь — это удобный alias,
  который резолвится в `fileId`.
- Дерево и теги — в базовом API (`/api/files`, `/api/folders`), а не в
  админском фасаде. Фасад остаётся для сырого S3.
- Все ответы — additive: новые поля не ломают существующих клиентов.
- Ошибки — `ProblemDetails` с машинным `code` в `extensions`
  (`FOLDER_NAME_CONFLICT`, `FOLDER_NOT_EMPTY`, `AMBIGUOUS_PATH` и т.п.).
- Изменения пишутся в аудит: `folder.create`, `folder.rename`, `folder.move`,
  `folder.delete`, `file.move`, `file.bulk-move`, `file.bulk-tag`.
- Для перемещения/переименования — транзакция и проверка цикла. При
  необходимости — оптимистическая блокировка (`If-Match` / `expectedVersion`).

### 4.2 Новые эндпоинты

Приоритеты: **P0** — минимальный рабочий каталог, **P1** — удобство,
**P2** — масштабирование и необязательные сценарии.

| P | Метод | Путь | Назначение |
|---|---|---|---|
| P0 | `GET` | `/api/folders?parentId=` | Дети папки; без `parentId` — корень |
| P0 | `GET` | `/api/folders/tree?depth=2` | Дерево для сайдбара; `depth` ограничен |
| P0 | `POST` | `/api/folders` | Создать `{ parentId?, name }` |
| P0 | `GET` | `/api/folders/{folderId}` | Папка + breadcrumbs + счётчики |
| P0 | `PATCH` | `/api/folders/{folderId}` | Переименовать `{ name }` |
| P0 | `POST` | `/api/folders/{folderId}/move` | Переместить `{ targetParentId? }` |
| P0 | `DELETE` | `/api/folders/{folderId}` | Удалить пустую папку; иначе `409` |
| P0 | `GET` | `/api/folders/{folderId}/contents` | Дети + файлы + breadcrumbs одним запросом |
| P0 | `GET` | `/api/folders/by-path?path=` | Резолв папки по пути |
| P0 | `GET` | `/api/files/resolve?path=` | Резолв файла по полному пути в `fileId` |
| P0 | `GET` | `/api/files/tags?folderId=&includeSubfolders=` | Фасеты тегов: ключ, значение, count, bytes |
| P1 | `POST` | `/api/files/{fileId}/move` | Переместить один файл `{ folderId }` |
| P1 | `POST` | `/api/files/bulk-move` | Массовое перемещение |
| P1 | `POST` | `/api/files/bulk-tag` | Массово добавить/удалить теги |
| P1 | `POST` | `/api/folders/ensure-path` | Создать недостающие папки по пути (для upload) |
| P1 | `GET` | `/api/files/facets?...` | Фасеты статусов, типов, владельцев, retention |
| P2 | `DELETE` | `/api/folders/{folderId}?mode=recursive` | Асинхронное удаление поддерева |
| P2 | `GET` | `/api/folders/{folderId}/operations/{id}` | Статус фоновой операции |

Пояснения:

- **`contents` vs `/api/files?folderId=`.** `contents` — один запрос для
  экрана: папки, файлы, хлебные крошки, счётчики. `/api/files` с фильтрами
  остаётся для сложных выборок и пагинации. Оба используют один сервис, чтобы
  не дублировать правила.
- **`resolve` — только резолв.** Он возвращает `fileId` и метаданные, а
  скачивание остаётся `GET /api/files/{fileId}`. Так не дублируется потоковая
  логика и сохраняется канонический идентификатор. При желании можно добавить
  `GET /api/files/by-path?path=` как `302` на `/api/files/{fileId}`, но это
  отдельное решение.
- **`ensure-path`** нужен для drag&drop папок из браузера: клиент присылает
  относительный путь, сервер создаёт недостающие папки и загружает файлы.
  Альтернатива — `folderPath` + `createFolders=true` в metadata загрузки.

### 4.3 Расширения существующих эндпоинтов

| Метод | Путь | Что добавить |
|---|---|---|
| `POST` | `/api/files` | В JSON `metadata`: `folderId` или `folderPath` + `createFolders`; ответ — с `folderId` и `path` |
| `GET` | `/api/files` | `folderId`, `path`, `includeSubfolders`, `tag`, `tagKey`, `owner`, `expiresBefore`, `retentionHold`, новые значения `sort` |
| `GET` | `/api/files/{fileId}/metadata` | `folderId`, `path`, `breadcrumbs` (или отдельный запрос) |
| `POST` | `/api/files/{fileId}/copy` | `folderId` / `folderPath`; копия не обязана оставаться в той же папке |
| `POST` | `/api/uploads` | `folderId` / `folderPath`; сохраняется в сессии и применяется при `complete` |
| `GET` | `/api/storage/stats` | `groupBy=folder\|tag\|contentType\|status\|owner`, `folderId`, `includeSubfolders`; в ответе — `groups[]` |
| `GET` | `/api/admin/buckets/{bucket}/objects` | Без изменений; для сырого S3 оставить prefix-навигацию |
| `POST` | `/api/admin/buckets/{bucket}/folders` | Уже поддерживает вложенность через `prefix`; не смешивать с логическими папками |

Важно: **не добавляйте `folderId` в `PATCH /metadata` как nullable-поле.**
`null` там уже означает «не менять», и получится неоднозначность «null = корень
или null = оставить». Для перемещения используйте отдельный `POST /move`.

### 4.4 Примеры

Создать вложенную папку:

```http
POST /api/folders
Content-Type: application/json
X-Reon-Actor: portal

{ "parentId": "0193f...", "name": "2026" }
```

```json
{
  "folderId": "0193f...",
  "parentId": "0193e...",
  "name": "2026",
  "path": "contracts/2026",
  "depth": 2,
  "childFolderCount": 0,
  "fileCount": 0,
  "totalBytes": 0,
  "createdAt": "2026-11-03T12:00:00+00:00",
  "updatedAt": "2026-11-03T12:00:00+00:00"
}
```

Содержимое папки:

```http
GET /api/folders/{folderId}/contents?page=1&pageSize=50&sort=originalName
```

```json
{
  "folder": { "folderId": "0193f...", "path": "contracts/2026", "...": "..." },
  "breadcrumbs": [
    { "folderId": null, "name": "Корень", "path": "" },
    { "folderId": "0193e...", "name": "contracts", "path": "contracts" },
    { "folderId": "0193f...", "name": "2026", "path": "contracts/2026" }
  ],
  "folders": [ { "folderId": "01940...", "name": "Q4", "path": "contracts/2026/Q4", "...": "..." } ],
  "files": {
    "items": [
      {
        "fileId": "01941...",
        "originalName": "a.pdf",
        "folderId": "0193f...",
        "path": "contracts/2026/a.pdf",
        "contentType": "application/pdf",
        "size": 1024,
        "tags": { "kind": "contract", "year": "2026" },
        "status": "Ready",
        "expiresAt": null
      }
    ],
    "total": 1,
    "page": 1,
    "pageSize": 50
  }
}
```

Резолв по классическому пути:

```http
GET /api/files/resolve?path=contracts/2026/a.pdf
```

```json
{
  "fileId": "01941...",
  "path": "contracts/2026/a.pdf",
  "metadata": { "...": "..." }
}
```

Если есть дубли имён:

```json
{
  "type": "https://.../ambiguous-path",
  "title": "Ambiguous path",
  "status": 409,
  "detail": "More than one file matches the path.",
  "code": "AMBIGUOUS_PATH",
  "candidates": [
    { "fileId": "01941...", "createdAt": "2026-11-01T10:00:00+00:00" },
    { "fileId": "01942...", "createdAt": "2026-11-02T10:00:00+00:00" }
  ]
}
```

### 4.5 Группировка и извлечение

| Сценарий | Как |
|---|---|
| Навигация по иерархии | `GET /api/folders/{id}/contents`, `GET /api/folders/tree` |
| Фильтр по папке | `GET /api/files?folderId=...` |
| Включая подпапки | `includeSubfolders=true` (JOIN по `folders.path`) |
| Фильтр по тегам | `tag=kind:contract&tag=year:2026` (AND), `tagAny=...` (OR) |
| Фасеты тегов | `GET /api/files/tags?...` |
| Группировка по каталогам/тегам/типам | `GET /api/storage/stats?groupBy=...` |
| Поиск по имени | `originalName` (уже есть) + trigram-индекс |
| Сортировка | `sort=originalName\|size\|createdAt\|updatedAt\|expiresAt` + `order` |

Клиентская группировка по одной странице — плохая идея при server-side
пагинации: она покажет «итоги» только по загруженным 50 строкам. Для «Сводки»
используйте отдельный агрегатный эндпоинт, а не группировку `ReonTable`.

## 5. Реализация на backend

### 5.1 Домен и слои

Добавить по существующим правилам:

- `Reon.Core/Entities/Folder.cs` — сущность с методами `Rename`, `Move`,
  `EnsureDepth`, `BuildPath`.
- `Reon.Core/Interfaces/IFolderRepository.cs` — `GetById`, `GetChildren`,
  `GetByPath`, `GetSubtree`, `Add`, `Remove`, `SaveChanges`.
- `Reon.Core/Services/FolderService.cs` — создание, переименование,
  перемещение, удаление, резолв пути, проверка циклов.
- `Reon.Core/Models/FolderModels.cs` — `FolderListQuery`, `FolderContents`,
  `FolderPath`, `CreateFolderCommand`, `MoveFolderCommand`.
- `Reon.Infrastructure/Persistence/FolderRepository.cs` и
  `Configurations/FolderConfiguration.cs`.
- `Reon.Api/Controllers/FoldersController.cs`.
- `Reon.Api/Contracts/FolderContracts.cs`.
- `FileMetadata` — `FolderId`, навигация `Folder?`, метод `MoveToFolder`.
- `FileStorageService` — принимать `folderId`/`folderPath` при upload/copy,
  проверять существование папки, создавать папку при `createFolders`.
- `MultipartUploadService` — сохранять `folderId` в сессии и применять при
  завершении.
- `ReonDbContext` — `DbSet<Folder>`.
- Исключения: `FolderNotFoundException`, `FolderNameConflictException`,
  `FolderNotEmptyException`, `FolderCycleException`,
  `AmbiguousPathException`, `InvalidPathException`.
- `CoreExceptionHandler` — маппинг в `ProblemDetails` с кодами.

### 5.2 Транзакции и конкурентность

- Создание: `INSERT` + уникальный индекс; конфликт → `409`.
- Переименование: `SELECT ... FOR UPDATE` папки, проверка нового пути,
  `UPDATE folders SET name=..., path=..., path_normalized=...` и обновление
  поддерева:
  ```sql
  UPDATE folders
  SET path = @newPath || substring(path FROM length(@oldPath) + 1),
      depth = depth + @depthDelta
  WHERE bucket = @bucket
    AND (path = @oldPath OR path LIKE @oldPath || '/%');
  ```
  Всё в одной транзакции.
- Перемещение: проверить, что `targetParentId` не сама папка и не её потомок
  (`target.path = old.path OR target.path LIKE old.path || '/%'`), затем
  обновить поддерево.
- Удаление: по умолчанию только пустая папка (`NOT EXISTS` дочерних папок и
  файлов со статусом не `Deleted`). Иначе `409 FOLDER_NOT_EMPTY`.
- Для массовых операций — ограничение batch (например, 100 файлов) и
  `ContinueOnError`, как в `bulk-delete`.

### 5.3 Аудит и события

- `folder.create` / `folder.rename` / `folder.move` / `folder.delete`
- `file.move` / `file.bulk-move` / `file.bulk-tag`
- В `details` писать `fromPath`, `toPath`, `parentId`, `folderId`.
- Если нужен live-UI, публиковать `folder.changed` / `file.changed` через
  существующий `IStorageEventPublisher` или отдельный канал. Не обязательно
  для MVP.

### 5.4 Тесты

- Юнит-тесты `FolderService`: путь, глубина, конфликт имён, цикл, перенос
  поддерева, удаление непустой папки.
- Тесты `FileStorageService`: upload в папку, copy в папку, move без S3,
  multipart с папкой.
- Тесты репозитория на реальном Postgres (если есть интеграционный контур):
  уникальный индекс, `LIKE` по поддереву, GIN по тегам, фасеты.
- Контрактные тесты контроллеров: коды ошибок, `ProblemDetails`.

## 6. Frontend: что делать в `reon3_admin_panel`

Сейчас `ObjectBrowser` — это сырой S3-браузер: `prefix`, `delimiter`,
`continuationToken`, создание папки-маркера и загрузка в префикс. Его не
нужно ломать. Нужно добавить **режим «Каталог»** и оставить **режим «S3»** для
администраторов.

### 6.1 Маршруты и экраны

```text
/object-storage/buckets/:bucket/files                 # корень каталога
/object-storage/buckets/:bucket/files/*path           # глубокий путь (deep link)
/object-storage/buckets/:bucket/files/object/:fileId  # карточка файла
/object-storage/buckets/:bucket/objects               # существующий S3-браузер
```

- В `app.routes.ts` добавить ветку `files` рядом с `objects`.
- `path` в URL — для шаринга и back/forward. При загрузке страницы
  резолвить через `GET /api/folders/by-path?path=...`; если папки нет —
  `404`-состояние.
- В `Bucket`-странице добавить переключатель «Каталог / S3-объекты».
  В «Каталоге» скрывать сырые ключи, версии и S3-теги; в «S3» — оставить как
  есть с предупреждением, что это технический режим.

### 6.2 Компоненты

| Компонент | Ответственность |
|---|---|
| `FolderTree` | Ленивое дерево: `GET /api/folders?parentId=`, раскрытие, контекстное меню, выбранная папка |
| `FolderBreadcrumbs` | Хлебные крошки по `breadcrumbs` из `contents`; не путать с route-breadcrumbs из `layout` |
| `CatalogBrowser` | Таблица «папки + файлы», `ReonTableComponent`, server pagination/sort/filter |
| `FolderDialog` | Создать/переименовать/переместить; валидация сегмента и конфликты |
| `MoveDialog` | Выбор целевой папки для файлов/папки; без drag&drop как единственного способа |
| `TagFilter` | Чипы фасетов, поиск по ключу/значению, несколько фильтров |
| `TagEditor` | Редактирование тегов в карточке файла (Postgres, не S3) |
| `UploadDialog` | Целевая папка, несколько файлов, `webkitdirectory` для загрузки папки, conflictPolicy |
| `BulkToolbar` | Массовые move/tag/delete для выбранных строк |

`ReonTableComponent` уже используется в `ObjectBrowser`; для каталога
пагинация будет `page/pageSize`, а не continuation token. Это нормально:
S3-листинг и каталожный листинг — разные источники.

### 6.3 Data-access

Добавить в `object-storage/data-access`:

- `models/file-catalog.models.ts`:
  `FolderResponse`, `FolderTreeNode`, `Breadcrumb`, `FolderContentsResponse`,
  `FileMetadataResponse`, `TagFacetResponse`, `ResolvePathResponse`,
  `MoveFileRequest`, `BulkMoveRequest`, `BulkTagRequest`.
- `file-catalog-api.service.ts` или методы в `ObjectStorageApiService`:
  - `listFolders(parentId?)`
  - `getFolderTree(depth?)`
  - `getFolder(folderId)`
  - `createFolder(request)`
  - `renameFolder(folderId, request)`
  - `moveFolder(folderId, request)`
  - `deleteFolder(folderId)`
  - `getFolderContents(folderId, query)`
  - `resolveFolderPath(path)`
  - `resolveFilePath(path)`
  - `uploadFiles(bucket?, folderId, files, conflictPolicy)` — если upload идёт через `/api/files`
  - `moveFiles(fileIds, folderId)`
  - `bulkTag(fileIds, add, removeKeys)`
  - `getTagFacets(query)`
  - `getStorageStats(groupBy, folderId?)`

Важно: не придумывать поля — сначала зафиксировать OpenAPI backend, затем
модели. AGENTS.md требует следовать реальному контракту.

### 6.4 UX

- Слева — дерево, справа — таблица. На узких экранах дерево сворачивается
  в drawer/аккордеон.
- Двойной клик по папке — вход; по файлу — карточка. Одинарный клик — выбор.
- Drag&drop файлов на папку — перемещение; но обязательно дублировать
  действием «Переместить в…» (доступность и touch).
- Мультивыбор + bulk toolbar: переместить, добавить теги, удалить.
- Фильтры: теги (чипы с count), статус, тип, владелец, срок; «Сбросить».
- Состояние папки/фильтров — в URL, чтобы ссылка открывала тот же экран.
- После rename/move обновлять затронутую ветку дерева и таблицу; не делать
  полный reload всего приложения.
- Пустые состояния: «В папке пока нет файлов», «Ничего не найдено по
  фильтрам», «Папка не найдена».
- Конфликты: диалог «Файл с таким именем уже есть» с выбором
  «Переименовать / Заменить / Отмена».
- В карточке файла: логический путь, папка, теги, статус, срок хранения.
  S3-ключ, версии и S3-теги — в блоке «Техническая информация» (только для
  админов).
- Не показывать `fileId` как основной способ навигации; он остаётся в
  технических деталях и для API.

### 6.5 Почему это стоит делать

- Пользователь получает привычный «проводник»: дерево, путь, хлебные крошки,
  поиск, теги, массовые операции — вместо плоского списка с `fileId`.
- Появляется шаринг: ссылка на папку/файл по пути, а не «найди по имени».
- Админ получает разделение: бизнес-каталог с retention и тегами отдельно от
  сырого S3 с версиями и lifecycle. Меньше риск удалить не то.
- Теги становятся рабочим инструментом группировки (фасеты, фильтры,
  сводки), а не декоративным полем.
- Перемещение/переименование не трогает S3, поэтому быстрые и безопасные.

## 7. Совместимость и миграция

- Существующие файлы: `folder_id = NULL`, то есть корень. Ничего не
  перемещается, S3-ключи не меняются.
- `GET /api/files/{fileId}` и все текущие эндпоинты продолжают работать.
- Новые поля в ответах — additive.
- Старый S3-браузер продолжает работать; он показывает `files/…` как
  технический префикс. Это ожидаемо.
- Обратной миграции данных нет: логическое дерево — новая метаинформация,
  а не переписывание ключей.
- Если позже понадобится «импорт» существующих путей из имени файла, это
  отдельная утилита: разобрать `originalName`, создать папки, проставить
  `folder_id`. Автоматически этого делать не стоит.

## 8. Этапы

1. **P0. Каркас каталога.**
   `folders`, `files.folder_id`, индексы, `FolderService`, CRUD папок,
   `GET /api/folders/{id}/contents`, `GET /api/files?folderId=`,
   `GET /api/files/resolve`, фильтры тегов, фасеты.
2. **P1. Загрузка и операции.**
   upload/copy/multipart в папку, `ensure-path`, move одного файла,
   bulk-move, bulk-tag, `stats?groupBy=`.
3. **P2. Frontend.**
   `FolderTree`, `CatalogBrowser`, `FolderDialog`, `MoveDialog`, `TagFilter`,
   `UploadDialog`, маршруты и карточка файла. Переключатель «Каталог / S3».
4. **P3. Масштабирование.**
   рекурсивное удаление через фоновую операцию, сохранённые представления,
   зеркалирование S3-тегов, мультибакетный каталог, корзина.

## 9. Решения, которые нужно подтвердить

1. **Один бакет или все?** Основной API сейчас работает с одним бакетом
   (`S3:BucketName`). Для MVP логическое дерево имеет смысл только для него.
   Если нужен каталог для всех бакетов, это отдельный этап: `bucket` в
   file-домене, bucket-aware `IObjectStorage`, и решение, что делать с
   объектами, загруженными через админский фасад (они не создают строки
   `files`). Нельзя смешивать «сырые объекты» и «каталог» без явного
   разделения.
2. **Уникальность имён файлов в папке?** Нужна для `resolve` по
   классическому пути. Если дубли допустимы, путь неоднозначен; тогда
   `resolve` возвращает `409` или `latest=true`.
3. **Кто пишет теги?** Если админка — источник, нужно перевести редактирование
   тегов с S3-тегов на `PATCH /api/files/{id}/metadata`. Иначе будут две
   несвязанные системы тегов.
4. **Нужна ли корзина?** Если да, `folders.deleted_at` и soft-delete, плюс
   правила восстановления. Для MVP можно удалять только пустые папки.
5. **Ограничения.** Максимальная глубина, число детей, размер папки,
   максимальная длина пути — лучше зафиксировать до реализации.
6. **Поиск.** Достаточно ли `ILIKE` + trigram, или нужен полнотекстовый поиск
   по имени/описанию/тегам.

## 10. Чего не делать

- Не строить основное дерево на S3-префиксах: переименование и перемещение
  станут дорогими и неатомарными.
- Не хранить путь только в `object_key`.
- Не фильтровать бизнес-файлы по S3-тегам через `ListObjectsV2`.
- Не делать группировку на клиенте по одной странице — только агрегатный
  эндпоинт.
- Не добавлять рекурсивное удаление без фоновой операции и лимитов.
- Не смешивать админский S3-браузер и бизнес-каталог в одном списке без
  явного переключателя.
