using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Reon.Api.OpenApi;

/// <summary>
/// Добавляет человекочитаемые описания и примеры запросов/ответов к
/// файловым эндпоинтам. Scalar показывает эти примеры рядом со схемой OpenAPI.
/// </summary>
public sealed class OpenApiExamplesOperationTransformer : IOpenApiOperationTransformer
{
    private const string FileIdExample = "019a7e4c-7f0e-7c3a-9a2b-5f5f9f9f9f9f";

    private const string FileExampleJson = """
        {
          "fileId": "019a7e4c-7f0e-7c3a-9a2b-5f5f9f9f9f9f",
          "originalName": "hello.txt",
          "contentType": "text/plain",
          "size": 13,
          "sha256": "8f4c2b7e6d5a1c3b9e0f2a4d6c8b1e3f5a7c9d0e2b4f6a8c1d3e5f7a9b0c2d4e",
          "eTag": "\"abc123\"",
          "owner": "system",
          "description": "пример",
          "tags": { "kind": "demo" },
          "status": "Ready",
          "version": 1,
          "failureReason": null,
          "createdAt": "2026-10-29T09:00:00+00:00",
          "completedAt": "2026-10-29T09:00:01+00:00",
          "updatedAt": "2026-10-29T09:00:01+00:00",
          "expiresAt": "2026-11-28T09:00:00+00:00",
          "retentionHold": false
        }
        """;

    private const string UploadSessionExampleJson = """
        {
          "uploadId": "019a7e4c-7f0e-7c3a-9a2b-5f5f9f9f9f9f",
          "fileId": "019a7e4c-0000-7000-8000-000000000001",
          "status": "Initiated",
          "partSize": 16777216,
          "minPartSize": 5242880,
          "maxParts": 10000,
          "createdAt": "2026-10-29T09:00:00+00:00",
          "expiresAt": "2026-11-05T09:00:00+00:00",
          "completedAt": null,
          "parts": [],
          "retentionSeconds": 604800
        }
        """;

    private const string PartExampleJson = """
        {
          "partNumber": 1,
          "eTag": "\"part-etag\"",
          "size": 16777216,
          "uploadedAt": "2026-10-29T09:00:00+00:00"
        }
        """;

    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var path = context.Description.RelativePath?.Trim('/');
        var method = context.Description.HttpMethod;

        if (path is null || method is null)
        {
            return Task.CompletedTask;
        }

        if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
            path.Equals("api/files", StringComparison.OrdinalIgnoreCase))
        {
            TransformUpload(operation);
        }
        else if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                 path.Equals("api/files/{fileId}", StringComparison.OrdinalIgnoreCase))
        {
            TransformDownload(operation);
        }
        else if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                 path.Equals("api/files/{fileId}/metadata", StringComparison.OrdinalIgnoreCase))
        {
            TransformMetadata(operation);
        }
        else if (method.Equals("DELETE", StringComparison.OrdinalIgnoreCase) &&
                 path.Equals("api/files/{fileId}", StringComparison.OrdinalIgnoreCase))
        {
            TransformDelete(operation);
        }
        else if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                 path.Equals("api/files", StringComparison.OrdinalIgnoreCase))
        {
            TransformList(operation);
        }
        else if (method.Equals("HEAD", StringComparison.OrdinalIgnoreCase) &&
                 path.Equals("api/files/{fileId}", StringComparison.OrdinalIgnoreCase))
        {
            TransformHead(operation);
        }
        else if (method.Equals("PATCH", StringComparison.OrdinalIgnoreCase) &&
                 path.Equals("api/files/{fileId}/metadata", StringComparison.OrdinalIgnoreCase))
        {
            TransformUpdateMetadata(operation);
        }
        else if (method.Equals("PATCH", StringComparison.OrdinalIgnoreCase) &&
                 path.Equals("api/files/{fileId}/retention", StringComparison.OrdinalIgnoreCase))
        {
            TransformUpdateRetention(operation);
        }
        else if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                 path.Equals("api/files/{fileId}/copy", StringComparison.OrdinalIgnoreCase))
        {
            TransformCopy(operation);
        }
        else if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                 path.Equals("api/files/bulk-delete", StringComparison.OrdinalIgnoreCase))
        {
            TransformBulkDelete(operation);
        }
        else if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                 path.Equals("api/uploads", StringComparison.OrdinalIgnoreCase))
        {
            TransformInitiateUpload(operation);
        }
        else if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                 path.Equals("api/uploads", StringComparison.OrdinalIgnoreCase))
        {
            TransformUploadList(operation);
        }
        else if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                 path.Equals("api/uploads/{uploadId}", StringComparison.OrdinalIgnoreCase))
        {
            TransformUploadGet(operation);
        }
        else if (method.Equals("PUT", StringComparison.OrdinalIgnoreCase) &&
                 path.Equals("api/uploads/{uploadId}/parts/{partNumber}", StringComparison.OrdinalIgnoreCase))
        {
            TransformUploadPart(operation);
        }
        else if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                 path.Equals("api/uploads/{uploadId}/complete", StringComparison.OrdinalIgnoreCase))
        {
            TransformUploadComplete(operation);
        }
        else if (method.Equals("DELETE", StringComparison.OrdinalIgnoreCase) &&
                 path.Equals("api/uploads/{uploadId}", StringComparison.OrdinalIgnoreCase))
        {
            TransformUploadAbort(operation);
        }
        else if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                 path.Equals("api/uploads/{uploadId}/abort", StringComparison.OrdinalIgnoreCase))
        {
            TransformUploadAbort(operation);
        }
        else if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                 path.Equals("api/storage/stats", StringComparison.OrdinalIgnoreCase))
        {
            TransformStorageStats(operation);
        }
        else if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                 path.Equals("api/storage/info", StringComparison.OrdinalIgnoreCase))
        {
            TransformStorageInfo(operation);
        }

        return Task.CompletedTask;
    }

    private static void TransformUpload(OpenApiOperation operation)
    {
        operation.Summary = "Загрузить файл";
        operation.Description =
            "Файл передаётся частью `File`. Необязательное поле `Metadata` — " +
            "JSON-строка с originalName, contentType, description, tags и " +
            "retentionSeconds. retentionSeconds задаёт срок хранения в секундах; " +
            "если поле не передано, применяется FileStorage:DefaultRetentionDays.";

        if (operation.RequestBody is { } requestBody &&
            requestBody.Content is { } requestContent &&
            requestContent.TryGetValue("multipart/form-data", out var form))
        {
            form.Example = new JsonObject
            {
                ["File"] = "(binary file content)",
                ["Metadata"] = """
                    {"originalName":"hello.txt","contentType":"text/plain","description":"пример","tags":{"kind":"demo"},"retentionSeconds":2592000}
                    """,
            };
        }

        SetJsonExample(
            operation,
            "201",
            """
            {
              "fileId": "019a7e4c-7f0e-7c3a-9a2b-5f5f9f9f9f9f",
              "originalName": "hello.txt",
              "contentType": "text/plain",
              "size": 13,
              "sha256": "8f4c2b7e6d5a1c3b9e0f2a4d6c8b1e3f5a7c9d0e2b4f6a8c1d3e5f7a9b0c2d4e",
              "eTag": "\"abc123\"",
              "owner": "system",
              "description": "пример",
              "tags": { "kind": "demo" },
              "status": "Ready",
              "version": 1,
              "failureReason": null,
              "createdAt": "2026-10-29T09:00:00+00:00",
              "completedAt": "2026-10-29T09:00:01+00:00",
              "updatedAt": "2026-10-29T09:00:01+00:00",
              "expiresAt": "2026-11-28T09:00:00+00:00",
              "retentionHold": false
            }
            """);
    }

    private static void TransformDownload(OpenApiOperation operation)
    {
        operation.Summary = "Скачать файл";
        operation.Description =
            "Возвращает содержимое файла бинарным потоком. Для скачивания достаточно " +
            "идентификатора файла (fileId) из ответа загрузки. Если срок хранения " +
            "истёк, возвращается 410 FILE_EXPIRED, даже когда объект ещё не удалён " +
            "из S3.";

        SetParameterExample(operation, "fileId", FileIdExample);
    }

    private static void TransformMetadata(OpenApiOperation operation)
    {
        operation.Summary = "Получить описание файла";
        operation.Description =
            "Возвращает JSON с описанием файла: именем, типом, размером, " +
            "статусом, тегами и служебными полями. Описание доступно и для " +
            "просроченного файла: status = Expired и expiresAt позволяют UI " +
            "показать, что срок истёк.";

        SetParameterExample(operation, "fileId", FileIdExample);
        SetJsonExample(
            operation,
            "200",
            """
            {
              "fileId": "019a7e4c-7f0e-7c3a-9a2b-5f5f9f9f9f9f",
              "originalName": "hello.txt",
              "contentType": "text/plain",
              "size": 13,
              "sha256": "8f4c2b7e6d5a1c3b9e0f2a4d6c8b1e3f5a7c9d0e2b4f6a8c1d3e5f7a9b0c2d4e",
              "eTag": "\"abc123\"",
              "owner": "system",
              "description": "пример",
              "tags": { "kind": "demo" },
              "status": "Ready",
              "version": 1,
              "failureReason": null,
              "createdAt": "2026-10-29T09:00:00+00:00",
              "completedAt": "2026-10-29T09:00:01+00:00",
              "updatedAt": "2026-10-29T09:00:01+00:00",
              "expiresAt": "2026-11-28T09:00:00+00:00",
              "retentionHold": false
            }
            """);
    }

    private static void TransformDelete(OpenApiOperation operation)
    {
        operation.Summary = "Удалить файл";
        operation.Description =
            "Удаляет объект из S3 и переводит описание в Deleted. Повторное " +
            "удаление идемпотентно: 204, 202 или 404.";

        SetParameterExample(operation, "fileId", FileIdExample);
    }

    private static void TransformList(OpenApiOperation operation)
    {
        operation.Summary = "Список файлов";
        operation.Description =
            "Возвращает страницу файлов с фильтрами по статусу, типу, имени и дате создания. " +
            "Максимальный размер страницы (pageSize) — 200.";
        SetJsonExample(
            operation,
            "200",
            """
            {
              "items": [
                {
                  "fileId": "019a7e4c-7f0e-7c3a-9a2b-5f5f9f9f9f9f",
                  "originalName": "hello.txt",
                  "contentType": "text/plain",
                  "size": 13,
                  "status": "Ready",
                  "createdAt": "2026-10-29T09:00:00+00:00"
                }
              ],
              "total": 1,
              "page": 1,
              "pageSize": 50
            }
            """);
    }

    private static void TransformHead(OpenApiOperation operation)
    {
        operation.Summary = "Заголовки файла";
        operation.Description =
            "Возвращает Content-Type, Content-Length и ETag без тела файла. " +
            "Если файл не в состоянии Ready, возвращается 409; если срок " +
            "хранения истёк — 410.";
        SetParameterExample(operation, "fileId", FileIdExample);
    }

    private static void TransformUpdateMetadata(OpenApiOperation operation)
    {
        operation.Summary = "Изменить описание файла";
        operation.Description =
            "Меняет originalName, description и tags. Значение null или отсутствующее поле " +
            "оставляет значение без изменений; пустая строка очищает description, {} очищает tags.";
        SetParameterExample(operation, "fileId", FileIdExample);
        SetJsonRequestBodyExample(
            operation,
            """
            {
              "originalName": "renamed.txt",
              "description": "новое описание",
              "tags": { "kind": "demo" }
            }
            """);
        SetJsonExample(operation, "200", FileExampleJson);
    }

    private static void TransformUpdateRetention(OpenApiOperation operation)
    {
        operation.Summary = "Изменить срок хранения";
        operation.Description =
            "retentionSeconds задаёт новый срок от текущего момента; " +
            "retentionHold запрещает или разрешает удаление по сроку. Продление " +
            "просроченного файла возвращает его в Ready, если объект ещё не удалён.";
        SetParameterExample(operation, "fileId", FileIdExample);
        SetJsonRequestBodyExample(
            operation,
            """
            {
              "retentionSeconds": 2592000,
              "retentionHold": false
            }
            """);
        SetJsonExample(operation, "200", FileExampleJson);
    }

    private static void TransformCopy(OpenApiOperation operation)
    {
        operation.Summary = "Скопировать файл";
        operation.Description =
            "Создаёт новый идентификатор файла (fileId) и копирует объект в S3 и " +
            "описание в PostgreSQL. Копия получает собственный срок хранения: " +
            "retentionSeconds в теле или FileStorage:DefaultRetentionDays.";
        SetParameterExample(operation, "fileId", FileIdExample);
        SetJsonRequestBodyExample(
            operation,
            """
            {
              "originalName": "copy.txt",
              "description": "копия",
              "tags": { "kind": "copy" },
              "retentionSeconds": 2592000
            }
            """);
        SetJsonExample(operation, "201", FileExampleJson);
    }

    private static void TransformBulkDelete(OpenApiOperation operation)
    {
        operation.Summary = "Удалить несколько файлов";
        operation.Description =
            "Удаляет до 100 уникальных fileId. Возвращает результат по каждому файлу.";
        SetJsonRequestBodyExample(
            operation,
            """
            {
              "fileIds": [
                "019a7e4c-7f0e-7c3a-9a2b-5f5f9f9f9f9f"
              ],
              "continueOnError": true
            }
            """);
        SetJsonExample(
            operation,
            "200",
            """
            {
              "items": [
                {
                  "fileId": "019a7e4c-7f0e-7c3a-9a2b-5f5f9f9f9f9f",
                  "status": "Deleted",
                  "httpStatus": 204,
                  "error": null
                }
              ]
            }
            """);
    }

    private static void TransformInitiateUpload(OpenApiOperation operation)
    {
        operation.Summary = "Начать параллельную загрузку файла по частям";
        operation.Description =
            "Создаёт сессию загрузки. Идентификатор загрузки S3 остаётся внутри сервиса; " +
            "клиент использует uploadId, partSize и expiresAt. Необязательный " +
            "retentionSeconds задаёт срок хранения будущего файла и применяется " +
            "при завершении загрузки.";
        SetJsonRequestBodyExample(
            operation,
            """
            {
              "originalName": "big.bin",
              "contentType": "application/octet-stream",
              "description": "большой файл",
              "tags": { "kind": "backup" },
              "retentionSeconds": 604800
            }
            """);
        SetJsonExample(operation, "201", UploadSessionExampleJson);
    }

    private static void TransformUploadList(OpenApiOperation operation)
    {
        operation.Summary = "Активные параллельные загрузки файла по частям";
        operation.Description = "Список незавершённых сессий загрузки.";
        SetJsonExample(
            operation,
            "200",
            $"[{UploadSessionExampleJson}]");
    }

    private static void TransformUploadGet(OpenApiOperation operation)
    {
        operation.Summary = "Статус параллельной загрузки файла по частям";
        operation.Description = "Возвращает статус, срок жизни и уже загруженные части.";
        SetParameterExample(operation, "uploadId", FileIdExample);
        SetJsonExample(operation, "200", UploadSessionExampleJson);
    }

    private static void TransformUploadPart(OpenApiOperation operation)
    {
        operation.Summary = "Загрузить часть";
        operation.Description =
            "Тело запроса — бинарные данные части. Content-Length обязателен. " +
            "Все части, кроме последней, должны быть не меньше 5 MiB.";
        SetParameterExample(operation, "uploadId", FileIdExample);
        SetParameterExample(operation, "partNumber", "1");
        SetJsonExample(operation, "200", PartExampleJson);
    }

    private static void TransformUploadComplete(OpenApiOperation operation)
    {
        operation.Summary = "Завершить параллельную загрузку файла по частям";
        operation.Description =
            "Собирает объект в S3, считает SHA-256 и создаёт описание файла в " +
            "PostgreSQL. Срок хранения берётся из сессии и отсчитывается от " +
            "момента завершения; в ответе возвращается expiresAt.";
        SetParameterExample(operation, "uploadId", FileIdExample);
        SetJsonExample(operation, "201", FileExampleJson);
    }

    private static void TransformUploadAbort(OpenApiOperation operation)
    {
        operation.Summary = "Прервать параллельную загрузку файла по частям";
        operation.Description =
            "Вызывает AbortMultipartUpload в S3. Повторное прерывание идемпотентно.";
        SetParameterExample(operation, "uploadId", FileIdExample);
    }

    private static void TransformStorageStats(OpenApiOperation operation)
    {
        operation.Summary = "Статистика хранилища";
        operation.Description = "Количество файлов и объём по статусам и типам.";
        SetJsonExample(
            operation,
            "200",
            """
            {
              "totalFiles": 10,
              "totalBytes": 104857600,
              "byStatus": { "Ready": 9, "Missing": 1 },
              "bytesByContentType": { "image/png": 104857000, "text/plain": 600 }
            }
            """);
    }

    private static void TransformStorageInfo(OpenApiOperation operation)
    {
        operation.Summary = "Информация о хранилище";
        operation.Description = "Бакет, лимиты и настройки параллельной загрузки файла по частям.";
        SetJsonExample(
            operation,
            "200",
            """
            {
              "bucket": "reon-files",
              "keyPrefix": "files/",
              "maxFileSizeBytes": 1073741824,
              "multipartPartSizeBytes": 16777216,
              "minMultipartPartSizeBytes": 5242880,
              "maxMultipartParts": 10000,
              "multipartUploadLifetimeDays": 7
            }
            """);
    }

    private static void SetJsonRequestBodyExample(
        OpenApiOperation operation,
        string json)
    {
        if (operation.RequestBody is { } requestBody &&
            requestBody.Content is { } content &&
            content.TryGetValue("application/json", out var mediaType))
        {
            mediaType.Example = JsonNode.Parse(json);
        }
    }

    private static void SetParameterExample(
        OpenApiOperation operation,
        string parameterName,
        string value)
    {
        foreach (var parameter in operation.Parameters ?? [])
        {
            if (parameter is OpenApiParameter concrete &&
                string.Equals(concrete.Name, parameterName, StringComparison.OrdinalIgnoreCase))
            {
                concrete.Example = JsonValue.Create(value);
                return;
            }
        }
    }

    private static void SetJsonExample(
        OpenApiOperation operation,
        string statusCode,
        string json)
    {
        if (operation.Responses is { } responses &&
            responses.TryGetValue(statusCode, out var response) &&
            response.Content is { } content &&
            content.TryGetValue("application/json", out var mediaType))
        {
            mediaType.Example = JsonNode.Parse(json);
        }
    }
}
