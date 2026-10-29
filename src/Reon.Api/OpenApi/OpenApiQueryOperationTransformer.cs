using System.Globalization;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.OpenApi;

namespace Reon.Api.OpenApi;

/// <summary>
/// Добавляет в OpenAPI операцию QUERY. Генератор ASP.NET Core 10 пропускает
/// методы, которых нет в OpenAPI 3.1, хотя сервер их обслуживает; OpenAPI 3.2
/// добавит query официально. Операция собирается из ApiDescription, чтобы не
/// дублировать параметры и схемы контроллера вручную.
/// </summary>
public sealed class OpenApiQueryOperationTransformer : IOpenApiDocumentTransformer
{
    private const string RelativePath = "api/admin/buckets/{bucket}/objects/{key}/presigned-urls";
    private const string DocumentPath = "/api/admin/buckets/{bucket}/objects/{key}/presigned-urls";

    public async Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        var description = context.DescriptionGroups
            .SelectMany(group => group.Items)
            .FirstOrDefault(item =>
                string.Equals(item.HttpMethod, "QUERY", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.RelativePath, RelativePath, StringComparison.OrdinalIgnoreCase));

        if (description is null)
        {
            return;
        }

        document.Paths ??= new OpenApiPaths();

        if (!document.Paths.TryGetValue(DocumentPath, out var pathItem) ||
            pathItem is not OpenApiPathItem concretePath)
        {
            concretePath = new OpenApiPathItem();
            document.Paths[DocumentPath] = concretePath;
        }

        concretePath.Operations ??= new();

        if (concretePath.Operations.ContainsKey(HttpMethod.Query))
        {
            return;
        }

        var operation = new OpenApiOperation
        {
            Summary = "Presigned URL",
            Description =
                "Создаёт временную подписанную ссылку на прямое скачивание. " +
                "Это безопасный запрос: объект не меняется, поэтому используется " +
                "метод QUERY (RFC 10008), а срок жизни передаётся телом.",
            Tags = new HashSet<OpenApiTagReference> { new("AdminObjects", document, null) },
            Parameters = [],
            Responses = new OpenApiResponses(),
        };

        foreach (var parameter in description.ParameterDescriptions)
        {
            if (parameter.Source == BindingSource.Body)
            {
                continue;
            }

            if (parameter.Type is null)
            {
                continue;
            }

            var isPath = parameter.Source == BindingSource.Path;

            operation.Parameters!.Add(new OpenApiParameter
            {
                Name = parameter.Name,
                In = isPath ? ParameterLocation.Path : ParameterLocation.Query,
                Required = isPath || parameter.IsRequired,
                Schema = await context.GetOrCreateSchemaAsync(
                    parameter.Type,
                    parameter,
                    cancellationToken),
            });
        }

        var body = description.ParameterDescriptions
            .FirstOrDefault(parameter => parameter.Source == BindingSource.Body);

        if (body?.Type is not null)
        {
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = body.IsRequired,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new()
                    {
                        Schema = await context.GetOrCreateSchemaAsync(
                            body.Type,
                            body,
                            cancellationToken),
                    },
                },
            };
        }

        foreach (var response in description.SupportedResponseTypes)
        {
            var openApiResponse = new OpenApiResponse
            {
                Description = ReasonPhrases.GetReasonPhrase(response.StatusCode),
            };

            if (response.Type is not null && response.Type != typeof(void))
            {
                var contentType = response.ApiResponseFormats.FirstOrDefault()?.MediaType
                    ?? "application/json";

                openApiResponse.Content = new Dictionary<string, OpenApiMediaType>
                {
                    [contentType] = new()
                    {
                        Schema = await context.GetOrCreateSchemaAsync(
                            response.Type,
                            null,
                            cancellationToken),
                    },
                };
            }

            operation.Responses![response.StatusCode.ToString(CultureInfo.InvariantCulture)] = openApiResponse;
        }

        concretePath.AddOperation(HttpMethod.Query, operation);
    }
}
