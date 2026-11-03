namespace Reon.Core.Entities;

/// <summary>
/// Строка реестра возможностей S3-провайдера. Хранится в Postgres, чтобы
/// значение, полученное из конфига или выученное по ошибке 501, переживало
/// перезапуск сервиса.
/// </summary>
public sealed class StorageCapabilityRecord
{
    public const string SourceConfig = "config";
    public const string SourceLearned = "learned";
    public const string SourceDefault = "default";

    private StorageCapabilityRecord()
    {
        // Требуется для EF Core.
    }

    public string Name { get; private set; } = string.Empty;

    public bool Supported { get; private set; }

    public string Source { get; private set; } = SourceConfig;

    public string? Details { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static StorageCapabilityRecord Create(
        string name,
        bool supported,
        string source,
        string? details,
        DateTimeOffset now)
    {
        return new StorageCapabilityRecord
        {
            Name = name,
            Supported = supported,
            Source = source,
            Details = details,
            UpdatedAt = now,
        };
    }

    public void Update(bool supported, string source, string? details, DateTimeOffset now)
    {
        Supported = supported;
        Source = source;
        Details = details;
        UpdatedAt = now;
    }
}
