using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Reon.Api.OpenApi;

/// <summary>
/// Применяет общие примеры значений к параметрам операций.
/// </summary>
public sealed class OpenApiParameterExamplesTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        OpenApiParameterExamples.Apply(operation);
        return Task.CompletedTask;
    }
}
