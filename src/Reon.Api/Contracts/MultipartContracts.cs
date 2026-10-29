using System.ComponentModel.DataAnnotations;

namespace Reon.Api.Contracts;

public sealed class InitiateMultipartUploadRequest
{
    [Required]
    public string OriginalName { get; init; } = string.Empty;

    public string? ContentType { get; init; }

    public string? Description { get; init; }

    public Dictionary<string, string>? Tags { get; init; }
}

public sealed record MultipartUploadResponse(
    Guid UploadId,
    Guid FileId,
    string Status,
    int PartSize,
    int MinPartSize,
    int MaxParts,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<MultipartPartResponse> Parts);

public sealed record MultipartPartResponse(
    int PartNumber,
    string ETag,
    long Size,
    DateTimeOffset UploadedAt);
