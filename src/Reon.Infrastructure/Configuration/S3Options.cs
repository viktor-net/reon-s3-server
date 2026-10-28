namespace Reon.Infrastructure.Configuration;

public sealed class S3Options
{
    public const string SectionName = "S3";

    /// <summary>Base URL of the S3-compatible endpoint, e.g. http://localhost:8080.</summary>
    public string ServiceUrl { get; set; } = "http://localhost:8080";

    public string AccessKey { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    public string Region { get; set; } = "us-east-1";

    public string BucketName { get; set; } = "reon-files";

    /// <summary>Required for Swift s3api and MinIO. Keep true unless the provider requires virtual-host style.</summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>Creates the bucket on startup when it does not exist.</summary>
    public bool EnsureBucket { get; set; } = true;
}
