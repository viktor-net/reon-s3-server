namespace Reon.Infrastructure.Configuration;

/// <summary>
/// Базовый срез возможностей S3-провайдера. Значения используются для
/// первоначального заполнения реестра в Postgres. Для Swift s3api
/// lifecycle, теги, bucket policy и Object Lock выключаются.
/// </summary>
public sealed class S3CapabilitiesOptions
{
    public bool Versioning { get; set; } = true;

    public bool ObjectTags { get; set; } = true;

    public bool Lifecycle { get; set; } = true;

    public bool BucketPolicy { get; set; } = true;

    public bool ObjectLock { get; set; } = true;
}
