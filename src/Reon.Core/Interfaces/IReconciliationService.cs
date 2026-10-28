namespace Reon.Core.Interfaces;

/// <summary>
/// Detects and repairs divergence between Postgres metadata and the object store.
/// </summary>
public interface IReconciliationService
{
    /// <summary>Finalizes or fails Pending rows that are older than the configured timeout.</summary>
    Task ReconcilePendingAsync(CancellationToken cancellationToken);

    /// <summary>Retries S3 deletion for rows stuck in Deleting.</summary>
    Task ReconcileDeletingAsync(CancellationToken cancellationToken);

    /// <summary>Removes soft-deleted rows after the configured retention period.</summary>
    Task PurgeDeletedAsync(CancellationToken cancellationToken);

    /// <summary>Deletes objects that have no metadata row and are older than the grace period.</summary>
    Task SweepOrphansAsync(CancellationToken cancellationToken);
}
