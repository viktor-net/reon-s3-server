namespace Reon.Core.Interfaces;

/// <summary>
/// Best-effort write guard: rejects a write that would exceed the configured
/// bucket quota. Usage is cached, so the check is advisory rather than exact.
/// </summary>
public interface IStorageQuotaGuard
{
    Task EnsureCanWriteAsync(
        string bucket,
        long additionalBytes,
        CancellationToken cancellationToken);
}
