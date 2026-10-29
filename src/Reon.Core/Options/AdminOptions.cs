namespace Reon.Core.Options;

public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    // How long a computed bucket usage stays cached, in seconds.
    public int UsageCacheSeconds { get; set; } = 60;

    // Upper bound for page size on administrative list endpoints.
    public int MaxPageSize { get; set; } = 200;

    // Maximum lifetime of a presigned URL (the S3 limit is 7 days).
    public int MaxPresignedUrlLifetimeSeconds { get; set; } = 604_800;

    // Upper bound of objects scanned when a substring search is requested.
    // S3 has no native search, so the service filters listed objects in memory.
    public int MaxSearchObjects { get; set; } = 5_000;

    // Actor recorded when the caller does not send X-Reon-Actor.
    public string DefaultActor { get; set; } = "system";

    // Interval of server heartbeat frames on /api/v1/admin/events, in seconds.
    public int EventHeartbeatSeconds { get; set; } = 30;
}
