namespace Reon.Core.Interfaces;

/// <summary>
/// Append-only audit trail shared by the file API and the admin facade.
/// Recording never throws: auditing must not mask the result of an operation.
/// </summary>
public interface IStorageAuditor
{
    Task RecordAsync(
        string actor,
        string action,
        string resource,
        AuditResult result,
        int? statusCode,
        object? details,
        CancellationToken cancellationToken);
}
