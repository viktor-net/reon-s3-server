using System.Text.RegularExpressions;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Reon.Api.OpenApi;

/// <summary>
/// Приводит заголовки и описания операций к одному абзацу: XML-комментарии
/// в коде многострочные, и без нормализации в документ попадают переносы
/// и отступы из исходников.
/// </summary>
public sealed partial class OpenApiTextTransformer : IOpenApiOperationTransformer
{
    private static readonly Dictionary<int, string> ResponseDescriptions = new()
    {
        [200] = "Успешно",
        [201] = "Создано",
        [202] = "Принято",
        [204] = "Без содержимого",
        [400] = "Некорректный запрос",
        [401] = "Требуется авторизация",
        [403] = "Доступ запрещён",
        [404] = "Не найдено",
        [405] = "Метод не поддерживается",
        [409] = "Конфликт",
        [410] = "Ресурс больше недоступен",
        [411] = "Требуется длина",
        [413] = "Слишком большой запрос",
        [415] = "Неподдерживаемый тип содержимого",
        [500] = "Внутренняя ошибка",
        [501] = "Не реализовано",
        [502] = "Ошибка шлюза",
        [503] = "Сервис недоступен",
    };

    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        operation.Summary = Collapse(operation.Summary);
        operation.Description = Collapse(operation.Description);

        if (operation.Responses is not null)
        {
            foreach (var (status, response) in operation.Responses)
            {
                if (int.TryParse(status, out var code) &&
                    ResponseDescriptions.TryGetValue(code, out var description))
                {
                    response.Description = description;
                }
            }
        }

        return Task.CompletedTask;
    }

    private static string? Collapse(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? value
            : Whitespace().Replace(value, " ").Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
