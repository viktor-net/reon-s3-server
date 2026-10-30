using Reon.Core.Enums;

namespace Reon.Core.Models;

public sealed record FileListQuery(
    int Page = 1,
    int PageSize = 50,
    FileStatus? Status = null,
    string? ContentType = null,
    string? OriginalName = null,
    DateTimeOffset? CreatedFrom = null,
    DateTimeOffset? CreatedTo = null,
    string? Sort = null);

public sealed record FileListResult(
    IReadOnlyList<FileMetadata> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record StorageStats(
    int TotalFiles,
    long TotalBytes,
    IReadOnlyDictionary<string, int> ByStatus,
    IReadOnlyDictionary<string, long> BytesByContentType);

public sealed record CopyFileCommand(
    string? OriginalName,
    string? Description,
    IReadOnlyDictionary<string, string>? Tags,
    int? RetentionSeconds = null);
