namespace Reon.Core.Models;

public sealed record UploadFileCommand(
    string OriginalName,
    string? ContentType,
    string? Description,
    IReadOnlyDictionary<string, string>? Tags);
