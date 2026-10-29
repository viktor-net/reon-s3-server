namespace Reon.Core.Services;

public sealed record NormalizedFileInput(
    string OriginalName,
    string ContentType,
    string? Description,
    JsonDocument? Tags);

/// <summary>
/// Общая валидация и генерация ключей для обычной и multipart-загрузки.
/// </summary>
public static class FileInput
{
    public static NormalizedFileInput Normalize(
        string? originalName,
        string? contentType,
        string? description,
        IReadOnlyDictionary<string, string>? tags,
        FileStorageOptions options)
    {
        return new NormalizedFileInput(
            NormalizeOriginalName(originalName, options),
            NormalizeContentType(contentType, options),
            NormalizeDescription(description, options),
            NormalizeTags(tags, options));
    }

    public static string BuildObjectKey(
        Guid fileId,
        string originalName,
        FileStorageOptions options)
    {
        return $"{options.KeyPrefix}{fileId:N}{GetSafeExtension(originalName)}";
    }

    public static string NormalizeOriginalName(string? value, FileStorageOptions options)
    {
        var candidate = (value ?? string.Empty).Replace('\\', '/');
        candidate = Path.GetFileName(candidate) ?? string.Empty;
        candidate = new string(candidate.Where(character => !char.IsControl(character)).ToArray()).Trim();

        if (candidate.Length == 0)
        {
            candidate = "file";
        }

        if (candidate.Length > options.MaxOriginalNameLength)
        {
            candidate = candidate[..options.MaxOriginalNameLength];
        }

        return candidate;
    }

    public static string NormalizeContentType(string? value, FileStorageOptions options)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? "application/octet-stream" : value.Trim();

        if (candidate.Length > options.MaxContentTypeLength ||
            candidate.Contains('\r') ||
            candidate.Contains('\n'))
        {
            throw new FileValidationException("The content type is invalid.");
        }

        return candidate;
    }

    public static string? NormalizeDescription(string? value, FileStorageOptions options)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var candidate = value.Trim();
        if (candidate.Length > options.MaxDescriptionLength)
        {
            throw new FileValidationException(
                $"The description must not exceed {options.MaxDescriptionLength} characters.");
        }

        return candidate;
    }

    public static JsonDocument? NormalizeTags(
        IReadOnlyDictionary<string, string>? tags,
        FileStorageOptions options)
    {
        if (tags is null || tags.Count == 0)
        {
            return null;
        }

        if (tags.Count > options.MaxTags)
        {
            throw new FileValidationException($"At most {options.MaxTags} tags are allowed.");
        }

        foreach (var (key, value) in tags)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > options.MaxTagKeyLength)
            {
                throw new FileValidationException(
                    $"A tag key must be non-empty and at most {options.MaxTagKeyLength} characters.");
            }

            if (value is null || value.Length > options.MaxTagValueLength)
            {
                throw new FileValidationException(
                    $"A tag value must be non-null and at most {options.MaxTagValueLength} characters.");
            }
        }

        return JsonSerializer.SerializeToDocument(tags);
    }

    private static string GetSafeExtension(string originalName)
    {
        var extension = Path.GetExtension(originalName);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length < 2 || extension.Length > 16)
        {
            return string.Empty;
        }

        var candidate = extension.ToLowerInvariant();
        return candidate[1..].All(char.IsLetterOrDigit) ? candidate : string.Empty;
    }
}
