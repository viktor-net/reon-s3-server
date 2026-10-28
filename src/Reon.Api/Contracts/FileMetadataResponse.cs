using System.Text.Json;
using Reon.Core.Entities;

namespace Reon.Api.Contracts;

public sealed record FileMetadataResponse(
    Guid FileId,
    string OriginalName,
    string ContentType,
    long Size,
    string Sha256,
    string? ETag,
    string Owner,
    string? Description,
    IReadOnlyDictionary<string, string>? Tags,
    string Status,
    int Version,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset UpdatedAt)
{
    public static FileMetadataResponse FromEntity(FileMetadata file)
    {
        return new FileMetadataResponse(
            file.Id,
            file.OriginalName,
            file.ContentType,
            file.Size,
            file.Sha256,
            file.ETag,
            file.Owner,
            file.Description,
            DeserializeTags(file.Tags),
            file.Status.ToString(),
            file.Version,
            file.FailureReason,
            file.CreatedAt,
            file.CompletedAt,
            file.UpdatedAt);
    }

    private static Dictionary<string, string>? DeserializeTags(JsonDocument? tags)
    {
        if (tags is null)
        {
            return null;
        }

        try
        {
            return tags.RootElement.Deserialize<Dictionary<string, string>>();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
