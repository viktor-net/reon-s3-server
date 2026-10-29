using System.Data;
using System.Data.Common;

namespace Reon.Infrastructure.Reconciliation;

/// <summary>
/// Repairs the two divergence directions:
/// metadata row without an object, and object without a metadata row.
/// It never recreates missing bytes; it only records the loss and lets the
/// operator restore from a backup or S3 versioning.
/// </summary>
public sealed partial class ReconciliationService : IReconciliationService
{
    private const long AdvisoryLockKey = 0x5265_6F6E_5369_6E67; // "ReonSing"

    private readonly ReonDbContext _dbContext;
    private readonly IFileMetadataRepository _repository;
    private readonly IObjectStorage _objectStorage;
    private readonly IClock _clock;
    private readonly FileStorageOptions _options;
    private readonly ILogger<ReconciliationService> _logger;

    public ReconciliationService(
        ReonDbContext dbContext,
        IFileMetadataRepository repository,
        IObjectStorage objectStorage,
        IClock clock,
        IOptions<FileStorageOptions> options,
        ILogger<ReconciliationService> logger)
    {
        _dbContext = dbContext;
        _repository = repository;
        _objectStorage = objectStorage;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ReconcilePendingAsync(CancellationToken cancellationToken)
    {
        await using var lockHandle = await TryAcquireLockAsync(cancellationToken);
        if (lockHandle is null)
        {
            return;
        }

        var cutoff = _clock.UtcNow.AddMinutes(-_options.PendingTimeoutMinutes);
        var files = await _repository.GetByStatusOlderThanAsync(
            FileStatus.Pending,
            cutoff,
            limit: 100,
            cancellationToken);

        foreach (var file in files)
        {
            try
            {
                await ReconcilePendingFileAsync(file, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogFileReconciliationFailed(_logger, exception, file.Id);
            }
        }
    }

    public async Task ReconcileDeletingAsync(CancellationToken cancellationToken)
    {
        await using var lockHandle = await TryAcquireLockAsync(cancellationToken);
        if (lockHandle is null)
        {
            return;
        }

        var cutoff = _clock.UtcNow.AddMinutes(-_options.DeletingRetryMinutes);
        var files = await _repository.GetByStatusOlderThanAsync(
            FileStatus.Deleting,
            cutoff,
            limit: 100,
            cancellationToken);

        foreach (var file in files)
        {
            try
            {
                await _objectStorage.DeleteAsync(file.ObjectKey, cancellationToken);
                file.MarkDeleted(_clock.UtcNow);
                await _repository.SaveChangesAsync(cancellationToken);
                LogDeleteRetried(_logger, file.Id);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ObjectStorageException exception)
            {
                LogDeleteRetryFailed(_logger, exception, file.Id);
            }
        }
    }

    public async Task PurgeDeletedAsync(CancellationToken cancellationToken)
    {
        if (_options.DeletedRetentionDays <= 0)
        {
            return;
        }

        await using var lockHandle = await TryAcquireLockAsync(cancellationToken);
        if (lockHandle is null)
        {
            return;
        }

        var cutoff = _clock.UtcNow.AddDays(-_options.DeletedRetentionDays);
        var files = await _repository.GetDeletedOlderThanAsync(cutoff, limit: 100, cancellationToken);

        if (files.Count == 0)
        {
            return;
        }

        foreach (var file in files)
        {
            _repository.Remove(file);
        }

        await _repository.SaveChangesAsync(cancellationToken);
        LogPurged(_logger, files.Count);
    }

    public async Task SweepOrphansAsync(CancellationToken cancellationToken)
    {
        if (!_options.OrphanSweepEnabled)
        {
            return;
        }

        await using var lockHandle = await TryAcquireLockAsync(cancellationToken);
        if (lockHandle is null)
        {
            return;
        }

        var knownIds = (await _repository.GetActiveFileIdsAsync(cancellationToken)).ToHashSet();
        var cutoff = _clock.UtcNow.AddHours(-_options.OrphanGracePeriodHours);

        await foreach (var item in _objectStorage.ListAsync(_options.KeyPrefix, cancellationToken))
        {
            var fileId = ParseFileId(item.ObjectKey);
            if (fileId is null || knownIds.Contains(fileId.Value))
            {
                continue;
            }

            if (item.LastModified is null || item.LastModified > cutoff)
            {
                continue;
            }

            try
            {
                // ListObjectsV2 does not return user metadata. Verify that the
                // object was written by this service before deleting it.
                var head = await _objectStorage.HeadAsync(item.ObjectKey, cancellationToken);
                if (head is null)
                {
                    continue;
                }

                var storedFileId = GetMetadataValue(head, "reon-file-id");
                if (storedFileId is null ||
                    !Guid.TryParse(storedFileId, out var parsedFileId) ||
                    parsedFileId != fileId.Value)
                {
                    LogOrphanSkipped(_logger, item.ObjectKey);
                    continue;
                }

                await _objectStorage.DeleteAsync(item.ObjectKey, cancellationToken);
                LogOrphanDeleted(_logger, item.ObjectKey);
            }
            catch (ObjectStorageException exception)
            {
                LogOrphanDeleteFailed(_logger, exception, item.ObjectKey);
            }
        }
    }

    private async Task ReconcilePendingFileAsync(FileMetadata file, CancellationToken cancellationToken)
    {
        var head = await _objectStorage.HeadAsync(file.ObjectKey, cancellationToken);

        if (head is null)
        {
            file.MarkFailed("The object was not found in the object store during reconciliation.", _clock.UtcNow);
            await _repository.SaveChangesAsync(cancellationToken);
            LogPendingWithoutObject(_logger, file.Id);
            return;
        }

        var storedFileId = GetMetadataValue(head, "reon-file-id");
        if (storedFileId is not null &&
            (!Guid.TryParse(storedFileId, out var parsed) || parsed != file.Id))
        {
            await DeleteAndFailAsync(file, "The object metadata belongs to a different file.", cancellationToken);
            return;
        }

        if (head.Size != file.Size)
        {
            await DeleteAndFailAsync(file, "The stored object size does not match the metadata.", cancellationToken);
            return;
        }

        var storedSha256 = GetMetadataValue(head, "reon-sha256");
        if (storedSha256 is not null &&
            !string.Equals(storedSha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            await DeleteAndFailAsync(file, "The stored object checksum does not match the metadata.", cancellationToken);
            return;
        }

        file.MarkReady(head.ETag, head.VersionId, _clock.UtcNow);
        await _repository.SaveChangesAsync(cancellationToken);
        LogPendingFinalized(_logger, file.Id);
    }

    private async Task DeleteAndFailAsync(
        FileMetadata file,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await _objectStorage.DeleteAsync(file.ObjectKey, cancellationToken);
        }
        catch (ObjectStorageException exception)
        {
            LogOrphanDeleteFailed(_logger, exception, file.ObjectKey);
        }

        file.MarkFailed(reason, _clock.UtcNow);
        await _repository.SaveChangesAsync(cancellationToken);
        LogPendingRejected(_logger, file.Id, reason);
    }

    private static string? GetMetadataValue(ObjectInfo info, string key)
    {
        foreach (var (metadataKey, value) in info.Metadata)
        {
            if (string.Equals(metadataKey, key, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private Guid? ParseFileId(string objectKey)
    {
        if (!objectKey.StartsWith(_options.KeyPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var name = objectKey[_options.KeyPrefix.Length..];
        return Guid.TryParseExact(name, "N", out var id) ? id : null;
    }

    private async Task<IAsyncDisposable?> TryAcquireLockAsync(CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        await _dbContext.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_try_advisory_lock(@key)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "key";
            parameter.DbType = DbType.Int64;
            parameter.Value = AdvisoryLockKey;
            command.Parameters.Add(parameter);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is true)
            {
                return new AdvisoryLockHandle(_dbContext);
            }

            await _dbContext.Database.CloseConnectionAsync();
            LogLockNotAcquired(_logger);
            return null;
        }
        catch
        {
            await _dbContext.Database.CloseConnectionAsync();
            throw;
        }
    }

    private sealed class AdvisoryLockHandle(ReonDbContext dbContext) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                var connection = dbContext.Database.GetDbConnection();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT pg_advisory_unlock(@key)";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "key";
                parameter.DbType = DbType.Int64;
                parameter.Value = AdvisoryLockKey;
                command.Parameters.Add(parameter);
                await command.ExecuteScalarAsync();
            }
            finally
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }

    [LoggerMessage(EventId = 2201, Level = LogLevel.Warning, Message = "Reconciliation failed for file {FileId}.")]
    private static partial void LogFileReconciliationFailed(ILogger logger, Exception exception, Guid fileId);

    [LoggerMessage(EventId = 2202, Level = LogLevel.Information, Message = "Deleting file {FileId} was retried successfully.")]
    private static partial void LogDeleteRetried(ILogger logger, Guid fileId);

    [LoggerMessage(EventId = 2203, Level = LogLevel.Warning, Message = "Deleting file {FileId} failed again.")]
    private static partial void LogDeleteRetryFailed(ILogger logger, Exception exception, Guid fileId);

    [LoggerMessage(EventId = 2204, Level = LogLevel.Information, Message = "Purged {Count} soft-deleted metadata rows.")]
    private static partial void LogPurged(ILogger logger, int count);

    [LoggerMessage(EventId = 2205, Level = LogLevel.Information, Message = "Deleted orphan object {ObjectKey}.")]
    private static partial void LogOrphanDeleted(ILogger logger, string objectKey);

    [LoggerMessage(EventId = 2206, Level = LogLevel.Warning, Message = "Failed to delete orphan object {ObjectKey}.")]
    private static partial void LogOrphanDeleteFailed(ILogger logger, Exception exception, string objectKey);

    [LoggerMessage(EventId = 2207, Level = LogLevel.Warning, Message = "Pending file {FileId} has no object; marked Failed.")]
    private static partial void LogPendingWithoutObject(ILogger logger, Guid fileId);

    [LoggerMessage(EventId = 2208, Level = LogLevel.Information, Message = "Pending file {FileId} was finalized to Ready.")]
    private static partial void LogPendingFinalized(ILogger logger, Guid fileId);

    [LoggerMessage(EventId = 2209, Level = LogLevel.Warning, Message = "Pending file {FileId} was rejected: {Reason}")]
    private static partial void LogPendingRejected(ILogger logger, Guid fileId, string reason);

    [LoggerMessage(EventId = 2210, Level = LogLevel.Debug, Message = "Reconciliation skipped because another instance holds the advisory lock.")]
    private static partial void LogLockNotAcquired(ILogger logger);

    [LoggerMessage(EventId = 2211, Level = LogLevel.Warning, Message = "Skipped orphan candidate {ObjectKey}: it was not created by this service.")]
    private static partial void LogOrphanSkipped(ILogger logger, string objectKey);
}
