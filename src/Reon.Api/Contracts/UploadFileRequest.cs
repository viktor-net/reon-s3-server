using System.ComponentModel.DataAnnotations;

namespace Reon.Api.Contracts;

public sealed class UploadFileRequest
{
    [Required]
    public IFormFile File { get; set; } = default!;

    /// <summary>
    /// Необязательный JSON-объект с originalName, contentType, description,
    /// tags и retentionSeconds. Строка JSON изолирует API от заголовков S3
    /// и имён полей формы.
    /// </summary>
    public string? Metadata { get; set; }
}

public sealed record UploadMetadata
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public string? OriginalName { get; init; }

    public string? ContentType { get; init; }

    public string? Description { get; init; }

    public Dictionary<string, string>? Tags { get; init; }

    /// <summary>
    /// Срок хранения в секундах. Не передан — используется
    /// FileStorage:DefaultRetentionDays; null в JSON означает бессрочно,
    /// если это разрешено настройками.
    /// </summary>
    public int? RetentionSeconds { get; init; }

    public static UploadMetadata? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<UploadMetadata>(json, JsonOptions);
    }
}
