namespace Reon.Api.Contracts;

public sealed record FileListResponse(
    IReadOnlyList<FileMetadataResponse> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record UpdateFileMetadataRequest(
    string? OriginalName,
    string? Description,
    Dictionary<string, string>? Tags);

public sealed record CopyFileRequest(
    string? OriginalName,
    string? Description,
    Dictionary<string, string>? Tags,
    int? RetentionSeconds = null);

/// <summary>
/// Изменение срока хранения. retentionSeconds продлевает срок от текущего
/// момента; retentionHold запрещает или разрешает удаление по сроку.
/// </summary>
public sealed record UpdateRetentionRequest(
    int? RetentionSeconds = null,
    bool? RetentionHold = null);

public sealed record BulkDeleteRequest(
    IReadOnlyList<Guid> FileIds,
    bool ContinueOnError = true);

public sealed record BulkDeleteItem(
    Guid FileId,
    string Status,
    int HttpStatus,
    string? Error);

public sealed record BulkDeleteResponse(
    IReadOnlyList<BulkDeleteItem> Items);

public sealed record StorageStatsResponse(
    int TotalFiles,
    long TotalBytes,
    IReadOnlyDictionary<string, int> ByStatus,
    IReadOnlyDictionary<string, long> BytesByContentType);
