# Postgres и S3: как сервис хранит файлы

Документ описывает разделение ответственности между реляционной базой и
объектным хранилищем, порядок записи и удаления, а также поведение при сбоях.
Он дополняет `README.md`.

## 1. Роли

| Компонент | Что хранит | Источник истины |
|---|---|---|
| PostgreSQL | Описание файла, статус, срок хранения, аудит, настройки бакетов | Да, по метаданным и жизненному циклу |
| S3 | Байты объекта и служебные metadata `reon-file-id`, `reon-sha256` | Да, по содержимому |
| Сервис | Правила переходов, проверка квоты, сверка, сборщик retention | Единственный писатель в S3 |

Клиенты не получают доступ к S3 и не знают ключ объекта. В базе хранится
`object_key`, а не URL. Наружу отдаётся `fileId` и REST-эндпоинт сервиса.

## 2. Таблицы

| Таблица | Назначение |
|---|---|
| `files` | Одна строка на файл: описание, `object_key`, `sha256`, статус, `expires_at`, `retention_hold` |
| `multipart_uploads` | Сессия параллельной загрузки: `upload_id` S3, будущий `file_id`, `retention_seconds` |
| `multipart_upload_parts` | Загруженные части: номер, ETag, размер |
| `bucket_settings` | Квота и режим доступа бакета |
| `audit_events` | Журнал операций файлового API и админского фасада |

Таблицы и колонки описаны DDL-комментариями в миграции `AddDatabaseComments`.
Их видно в `psql`:

```sql
\d+ files
SELECT column_name, col_description('public.files'::regclass, ordinal_position)
FROM information_schema.columns
WHERE table_schema = 'public' AND table_name = 'files'
ORDER BY ordinal_position;
```

## 3. Обычная загрузка

```text
Клиент → API: POST /api/files (файл + metadata)
API → temp-файл: размер и SHA-256
API → Postgres: INSERT files (Pending, expires_at)
API → S3: PutObject (metadata reon-file-id, reon-sha256)
API → Postgres: UPDATE files (Ready, etag, completed_at)
API → Клиент: 201 + fileId + expiresAt
```

Правила:

1. Строка `Pending` создаётся до записи в S3 — это резерв `fileId` и ключа.
2. Ключ детерминирован: `files/{fileId:N}{ext}`. Повторная попытка не создаёт
   второй объект для того же `fileId`.
3. Если S3 упал, строка становится `Failed`, а сервис пытается удалить объект.
4. Если S3 записался, а финальный `SaveChanges` упал, строка остаётся `Pending`;
   сверка проверит объект по `reon-file-id`, размеру и `reon-sha256`.
5. `expires_at` вычисляется при создании строки: `now + retentionSeconds` или
   `null`, если срок не задан. Для multipart — при завершении загрузки.

## 4. Параллельная загрузка

```text
Клиент → API: POST /api/uploads (retentionSeconds)
API → S3: InitiateMultipartUpload
API → Postgres: INSERT multipart_uploads (retention_seconds)
Клиент → API: PUT /parts/{n}
API → S3: UploadPart
API → Postgres: UPSERT multipart_upload_parts
Клиент → API: POST /complete
API → S3: CompleteMultipartUpload
API → Postgres: INSERT files (Ready, expires_at) + UPDATE multipart_uploads (Completed)
```

`expires_at` будущего файла считается от завершения, а не от начала загрузки:
иначе долгая загрузка сокращала бы срок хранения. Срок жизни самой сессии —
отдельное поле `multipart_uploads.expires_at`.

## 5. Чтение

```text
Клиент → API: GET /api/files/{fileId}
API → Postgres: SELECT files
API: status = Deleted → 404; expires_at <= now и hold = false → 410
API: status != Ready → 409
API → S3: GetObject
API → Клиент: поток байтов
```

- Проверка срока выполняется в API, а не в S3. Поэтому `410` появляется сразу,
  даже если объект ещё не удалён сборщиком.
- `GET /api/files/{fileId}/metadata` отдаёт описание и для `Expired`, чтобы UI
  показал срок и предложил продление.
- `HEAD` для `Expired` возвращает `410`, для неготового файла — `409`.
- `Content-Type` и `Content-Disposition` берутся из Postgres, а не из ключа S3.

## 6. Удаление

### Ручное

```text
DELETE → Postgres: status = Deleting
       → S3: DeleteObject (идемпотентно)
       → Postgres: status = Deleted, deleted_at
```

Если S3 недоступен, строка остаётся `Deleting`; сверка повторит удаление.
Строка `Deleted` живёт `DeletedRetentionDays` (по умолчанию 30 дней), затем
удаляется из Postgres. Байты к этому моменту уже удалены.

### По сроку хранения

```text
Сборщик → Postgres: SELECT Ready, expires_at <= now, hold = false
        → Postgres: status = Expired, expired_at
        → S3: DeleteObject
        → Postgres: status = Deleted, deleted_at
```

- `Expired` — нормальное состояние, а не потеря объекта.
- Если S3 недоступен, строка остаётся `Expired`; следующий проход повторит.
- `retention_hold = true` исключает файл из выборки. Снятие hold возвращает
  `Expired` в `Ready`, если объект ещё цел.
- Продление меняет `expires_at` и тоже может вернуть `Expired` в `Ready`.
- Сборщик берёт Postgres advisory lock, поэтому несколько инстансов не удаляют
  одно и то же параллельно.

## 7. Почему срок хранения в Postgres, а не в S3

| Механизм S3 | Почему не основной TTL |
|---|---|
| Lifecycle Expiration | Правило на префикс/тег/возраст, а не на отдельный `expires_at`; удаление асинхронное; нет hold, продления и `410` |
| Object Lock | Запрещает удалять **раньше** срока, то есть решает обратную задачу |
| HTTP `Expires` | Только подсказка для кэша |
| Swift `X-Delete-At` | Нативный Swift API, не S3-контракт; удаляет «молча», без статусов и аудита |

S3 lifecycle допустим только как страховка на отдельном временном префиксе
(например, `tmp/`). На `files/` он опасен: правило не различает бессрочные и
временные объекты.

## 8. Сверка и расхождения

| Ситуация | Поведение |
|---|---|
| `Pending`, объект есть | Проверка `reon-file-id`, размера и `reon-sha256`; совпало → `Ready`, иначе объект удаляется, строка → `Failed` |
| `Pending`, объекта нет | Строка → `Failed` |
| `Ready`, объекта нет | Строка → `Missing`, API отвечает `404`; байты не восстанавливаются |
| `Deleting`, объект есть | Повторное удаление |
| `Expired`, объект есть | Штатное состояние; сборщик повторит удаление |
| `Expired`, объекта нет | Сборщик переведёт строку в `Deleted` |
| Объект без строки | Orphan sweep удаляет после grace period, если ключ и `reon-file-id` подтверждают происхождение |
| `Failed`/`Corrupted`/`Deleted`, объект есть | Orphan sweep удаляет после grace period |

`Expired` считается активной строкой для orphan sweep: объект принадлежит
сервису до момента удаления сборщиком.

## 9. Квоты

Квота проверяется до записи в S3: для обычной загрузки — до создания строки
`Pending`, для multipart — при завершении. Проверка best-effort: параллельные
записи могут вместе пересечь квоту, а объекты, записанные в обход сервиса,
учитываются только после пересчёта.

Просроченные, но ещё не удалённые объекты занимают место и учитываются в квоте.
Задержка сборщика влияет на доступный объём; интервал и батч настраиваются.

## 10. Гарантии и сбои

| Сбой | Что происходит |
|---|---|
| Postgres недоступен | API отвечает `503/500`, S3-операции не начинаются |
| S3 недоступен при загрузке | Строка `Failed`; объект удаляется best-effort, остаток разберёт сверка |
| S3 недоступен при удалении | Строка остаётся `Deleting` или `Expired`; повтор в следующем проходе |
| Сбой после записи в S3 | Строка `Pending`; сверка подтвердит или отклонит объект |
| Сбой после удаления из S3 | Строка `Deleting`/`Expired`; повторное удаление идемпотентно |
| Два инстанса одновременно | Сверка и сборщик берут advisory lock; удаление объекта идемпотентно |

Сервис не пытается восстановить потерянные байты. Для этого нужны backup или
S3 versioning; сервис только фиксирует `Missing`/`Corrupted`.

## 11. Эксплуатационная проверка

- Убедиться, что на бакет с `files/` не повешено lifecycle-правило на удаление.
- Следить за количеством строк `Expired`: рост означает, что сборщик не
  успевает или S3 недоступен.
- Проверять логи `RetentionGcService`: ошибки удаления и пропуски advisory lock.
- Проверять `COMMENT ON` после миграций: `\d+ files` должен показывать описания.
- Не хранить presigned URL в базе, логах и событиях: только `object_key`.
