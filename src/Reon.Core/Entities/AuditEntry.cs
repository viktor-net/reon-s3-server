using Reon.Core.Enums;

namespace Reon.Core.Entities;

/// <summary>
/// One administrative or storage operation recorded by the service. The audit
/// log is append-only from the application's point of view.
/// </summary>
public sealed class AuditEntry
{
    private AuditEntry()
    {
        // Required by EF Core.
    }

    public Guid Id { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public string Actor { get; private set; } = string.Empty;

    /// <summary>Stable machine-readable action name, e.g. "object.upload".</summary>
    public string Action { get; private set; } = string.Empty;

    /// <summary>Affected resource: bucket name or "bucket/key".</summary>
    public string Resource { get; private set; } = string.Empty;

    public AuditResult Result { get; private set; }

    public int? StatusCode { get; private set; }

    public JsonDocument? Details { get; private set; }

    public static AuditEntry Create(
        Guid id,
        string actor,
        string action,
        string resource,
        AuditResult result,
        int? statusCode,
        JsonDocument? details,
        DateTimeOffset now)
    {
        return new AuditEntry
        {
            Id = id,
            OccurredAt = now,
            Actor = actor,
            Action = action,
            Resource = resource,
            Result = result,
            StatusCode = statusCode,
            Details = details,
        };
    }
}
