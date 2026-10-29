using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace Reon.Api.OpenApi;

/// <summary>
/// Подставляет примеры значений для параметров, у которых их нет. Scalar
/// использует example в Test Request, иначе в URL остаются шаблоны
/// вида {bucket} и {key}.
/// </summary>
internal static class OpenApiParameterExamples
{
    private static readonly Dictionary<string, string> Examples = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bucket"] = "cat-demo",
        ["key"] = "README.md",
        ["page"] = "1",
        ["pageSize"] = "50",
        ["sort"] = "name",
        ["maxKeys"] = "100",
        ["delimiter"] = "/",
        ["includeContentType"] = "false",
        ["refresh"] = "false",
        ["refreshUsage"] = "false",
        ["status"] = "Ready",
        ["actor"] = "admin",
        ["action"] = "object.upload",
        ["resource"] = "cat-demo/README.md",
        ["result"] = "Success",
    };

    public static void Apply(OpenApiOperation operation)
    {
        foreach (var parameter in operation.Parameters ?? [])
        {
            if (parameter is not OpenApiParameter concrete ||
                concrete.Name is null ||
                concrete.Example is not null)
            {
                continue;
            }

            if (Examples.TryGetValue(concrete.Name, out var value))
            {
                concrete.Example = JsonValue.Create(value);
            }
        }
    }
}
