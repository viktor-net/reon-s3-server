namespace Reon.Infrastructure.Configuration;

public sealed class S3Options
{
    public const string SectionName = "S3";

    /// <summary>Базовый URL S3-совместимого эндпоинта, например http://localhost:8080.</summary>
    public string ServiceUrl { get; set; } = "http://localhost:8080";

    public string AccessKey { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    public string Region { get; set; } = "us-east-1";

    public string BucketName { get; set; } = "reon-files";

    /// <summary>Нужно для Swift s3api и MinIO. Оставлять true, если провайдер не требует virtual-host стиля.</summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>Создаёт бакет при старте, если его нет.</summary>
    public bool EnsureBucket { get; set; } = true;
}
