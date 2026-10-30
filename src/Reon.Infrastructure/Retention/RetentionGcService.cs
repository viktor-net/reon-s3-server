using System.Data;
using System.Data.Common;

namespace Reon.Infrastructure.Retention;

/// <summary>
/// Двухфазная очистка файлов по сроку хранения. Сначала строка переводится
/// в Expired и сразу перестаёт отдаваться API, затем объект удаляется из S3.
/// Ошибка S3 оставляет строку в Expired: следующий проход повторит удаление.
/// </summary>
public sealed partial class RetentionGcService(
    ReonDbContext dbContext,
    IObjectStorage objectStorage,
    IClock clock,
    IOptions<FileStorageOptions> options,
    ILogger<RetentionGcService> logger)
{
    private const long AdvisoryLockKey = 0x5265_6F6E_5265_7431; // "ReonRet1"

    /// <summary>Переводит Ready в Expired для файлов, у которых наступил срок.</summary>
    public async Task<int> ExpireDueAsync(CancellationToken cancellationToken)
    {
        await using var lockHandle = await TryAcquireLockAsync(cancellationToken);
        if (lockHandle is null)
        {
            return 0;
        }

        var now = clock.UtcNow;
        var limit = Math.Max(1, options.Value.RetentionSweepBatchSize);

        var due = await dbContext.Files
            .Where(file =>
                file.Status == FileStatus.Ready &&
                !file.RetentionHold &&
                file.ExpiresAt != null &&
                file.ExpiresAt <= now)
            .OrderBy(file => file.ExpiresAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        foreach (var file in due)
        {
            file.MarkExpired(now);
        }

        if (due.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            LogExpired(logger, due.Count);
        }

        return due.Count;
    }

    /// <summary>Удаляет объекты файлов, уже переведённых в Expired.</summary>
    public async Task<int> DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        await using var lockHandle = await TryAcquireLockAsync(cancellationToken);
        if (lockHandle is null)
        {
            return 0;
        }

        var now = clock.UtcNow;
        var limit = Math.Max(1, options.Value.RetentionSweepBatchSize);

        var expired = await dbContext.Files
            .Where(file => file.Status == FileStatus.Expired && !file.RetentionHold)
            .OrderBy(file => file.ExpiredAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var deleted = 0;

        foreach (var file in expired)
        {
            try
            {
                await objectStorage.DeleteAsync(file.ObjectKey, cancellationToken);
                file.MarkDeleted(now);
                deleted++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogDeleteFailed(logger, exception, file.Id);
            }
        }

        if (deleted > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            LogDeleted(logger, deleted);
        }

        return deleted;
    }

    private async Task<IAsyncDisposable?> TryAcquireLockAsync(CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        await dbContext.Database.OpenConnectionAsync(cancellationToken);

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
                return new AdvisoryLockHandle(dbContext);
            }

            await dbContext.Database.CloseConnectionAsync();
            LogLockNotAcquired(logger);
            return null;
        }
        catch
        {
            await dbContext.Database.CloseConnectionAsync();
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

    [LoggerMessage(EventId = 5001, Level = LogLevel.Information, Message = "Marked {Count} files as expired by retention.")]
    private static partial void LogExpired(ILogger logger, int count);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Information, Message = "Deleted {Count} expired objects from S3.")]
    private static partial void LogDeleted(ILogger logger, int count);

    [LoggerMessage(EventId = 5003, Level = LogLevel.Warning, Message = "Failed to delete expired object for file {FileId}.")]
    private static partial void LogDeleteFailed(ILogger logger, Exception exception, Guid fileId);

    [LoggerMessage(EventId = 5004, Level = LogLevel.Debug, Message = "Retention sweep skipped because another instance holds the advisory lock.")]
    private static partial void LogLockNotAcquired(ILogger logger);
}
