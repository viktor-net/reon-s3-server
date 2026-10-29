using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Reon.Api.OpenApi;

/// <summary>
/// Adds human-readable descriptions and request/response examples to the
/// file endpoints. Scalar renders these examples next to the OpenAPI schema.
/// </summary>
public sealed class OpenApiExamplesOperationTransformer : IOpenApiOperationTransformer
{
    private const string FileIdExample = "019a7e4c-7f0e-7c3a-9a2b-5f5f9f9f9f9f";

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
            path.Equals("api/v1/files", StringComparison.OrdinalIgnoreCase))
        {
            TransformUpload(operation);
        }
        else if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                 path.Equals("api/v1/files/{fileId}", StringComparison.OrdinalIgnoreCase))
        {
            TransformDownload(operation);
        }
        else if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                 path.Equals("api/v1/files/{fileId}/metadata", StringComparison.OrdinalIgnoreCase))
        {
            TransformMetadata(operation);
        }
        else if (method.Equals("DELETE", StringComparison.OrdinalIgnoreCase) &&
                 path.Equals("api/v1/files/{fileId}", StringComparison.OrdinalIgnoreCase))
        {
            TransformDelete(operation);
        }

        return Task.CompletedTask;
    }

    private static void TransformUpload(OpenApiOperation operation)
    {
        operation.Summary = "Загрузить файл";
        operation.Description =
            "Файл передаётся частью `File`. Необязательное поле `Metadata` — " +
            "JSON-строка с originalName, contentType, description и tags.";

        if (operation.RequestBody is { } requestBody &&
            requestBody.Content is { } requestContent &&
            requestContent.TryGetValue("multipart/form-data", out var form))
        {
            form.Example = new JsonObject
            {
                ["File"] = "(binary file content)",
                ["Metadata"] = """
                    {"originalName":"hello.txt","contentType":"text/plain","description":"пример","tags":{"kind":"demo"}}
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
              "updatedAt": "2026-10-29T09:00:01+00:00"
            }
            """);
    }

    private static void TransformDownload(OpenApiOperation operation)
    {
        operation.Summary = "Скачать файл";
        operation.Description =
            "Возвращает содержимое файла бинарным потоком. Для получения файла " +
            "нужен только fileId из ответа загрузки.";

        SetParameterExample(operation, "fileId", FileIdExample);
    }

    private static void TransformMetadata(OpenApiOperation operation)
    {
        operation.Summary = "Получить описание файла";
        operation.Description =
            "Возвращает JSON с описанием файла: именем, типом, размером, " +
            "статусом, тегами и служебными полями.";

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
              "updatedAt": "2026-10-29T09:00:01+00:00"
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
