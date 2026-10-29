using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Reon.Api.OpenApi;

/// <summary>
/// Заполняет описания групп операций. ASP.NET Core собирает теги из имён
/// контроллеров, но не переносит в них summary класса, поэтому описание
/// контроллера задаётся здесь.
/// </summary>
public sealed class OpenApiTagDescriptionsTransformer : IOpenApiDocumentTransformer
{
    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.Ordinal)
    {
        ["AdminAudit"] = "Журнал административных и файловых операций.",
        ["AdminBuckets"] = "Бакеты: настройки, доступ, lifecycle, использование и папки.",
        ["AdminObjects"] = "Объекты: загрузка, метаданные, версии, теги и presigned URL.",
        ["AdminOverview"] = "Сводка по хранилищу: бакеты, объекты и объём.",
        ["Files"] = "Файловый API: загрузка, список, метаданные, копирование и удаление.",
        ["Storage"] = "Информация о хранилище и статистика по файлам.",
        ["Uploads"] = "Multipart-сессии: начало, части, завершение и отмена.",
    };

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (document.Tags is null)
        {
            return Task.CompletedTask;
        }

        foreach (var tag in document.Tags)
        {
            if (tag.Name is not null && Descriptions.TryGetValue(tag.Name, out var description))
            {
                tag.Description = description;
            }
        }

        return Task.CompletedTask;
    }
}
