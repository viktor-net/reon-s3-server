namespace Reon.Core.Models;

public sealed record FileContent(
    Stream Stream,
    string FileName,
    string ContentType,
    long Length,
    string? ETag);
