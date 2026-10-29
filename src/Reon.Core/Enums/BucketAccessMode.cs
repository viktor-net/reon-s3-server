namespace Reon.Core.Enums;

/// <summary>
/// Anonymous access mode for a bucket. Authorized access is governed by IAM
/// policies outside this service; this setting only controls anonymous reads.
/// </summary>
public enum BucketAccessMode
{
    Private = 0,
    PublicRead = 1,
}
