using System.ComponentModel.DataAnnotations;

namespace Reon.Api.Contracts;

public sealed class UploadFileRequest
{
    [Required]
    public IFormFile File { get; set; } = default!;

    /// <summary>
    /// Optional JSON object with originalName, contentType, description and tags.
    /// A JSON string keeps the facade independent from S3 headers and form field names.
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

    public static UploadMetadata? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<UploadMetadata>(json, JsonOptions);
    }
}
