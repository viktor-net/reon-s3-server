using System.Buffers;
using System.Security.Cryptography;

namespace Reon.Core.Services;

/// <summary>
/// Явные сессии параллельной загрузки файла по частям (multipart-загрузки).
/// Идентификатор загрузки S3 и протокол S3
/// остаются внутри инфраструктурного слоя; клиенты видят только наш uploadId.
/// </summary>
public sealed partial class MultipartUploadService : IMultipartUploadService
{
    private readonly IObjectStorage _objectStorage;
    private readonly IMultipartUploadRepository _uploadRepository;
    private readonly IFileMetadataRepository _fileRepository;
    private readonly ITempFileFactory _tempFiles;
    private readonly IClock _clock;
    private readonly FileStorageOptions _options;
    private readonly IStorageQuotaGuard _quotaGuard;
    private readonly ILogger<MultipartUploadService> _logger;

    /// <summary>
    /// Создаёт сервис сессий параллельной загрузки файла по частям
    /// (multipart-загрузки).
    /// </summary>
    /// <param name="objectStorage">Низкоуровневые операции S3: части, complete, abort.</param>
    /// <param name="uploadRepository">Сессии и части в Postgres.</param>
    /// <param name="fileRepository">Итоговая строка files после завершения.</param>
    /// <param name="tempFiles">Буферизация частей и подсчёт SHA-256.</param>
    /// <param name="clock">Время создания, завершения и истечения сессии.</param>
    /// <param name="options">Размер частей, лимиты и срок жизни сессии.</param>
    /// <param name="quotaGuard">Проверка квоты при завершении загрузки.</param>
    /// <param name="logger">Лог шагов и ошибок параллельной загрузки файла
    /// по частям (multipart-загрузки).</param>
    public MultipartUploadService(
        IObjectStorage objectStorage,
        IMultipartUploadRepository uploadRepository,
        IFileMetadataRepository fileRepository,
        ITempFileFactory tempFiles,
        IClock clock,
        IOptions<FileStorageOptions> options,
        IStorageQuotaGuard quotaGuard,
        ILogger<MultipartUploadService> logger)
    {
        _objectStorage = objectStorage;
        _uploadRepository = uploadRepository;
        _fileRepository = fileRepository;
        _tempFiles = tempFiles;
        _clock = clock;
        _options = options.Value;
        _quotaGuard = quotaGuard;
        _logger = logger;
    }

    public async Task<MultipartUpload> InitiateAsync(
        string originalName,
        string? contentType,
        string? description,
        IReadOnlyDictionary<string, string>? tags,
        CancellationToken cancellationToken,
        int? retentionSeconds = null)
    {
        var input = FileInput.Normalize(originalName, contentType, description, tags, _options);
        var retention = _options.ResolveRetention(retentionSeconds);
        var fileId = Guid.CreateVersion7();
        var objectKey = FileInput.BuildObjectKey(fileId, input.OriginalName, _options);
        var now = _clock.UtcNow;
        var expiresAt = now.AddDays(_options.MultipartUploadLifetimeDays);

        var stored = await _objectStorage.InitiateMultipartUploadAsync(
            objectKey,
            input.ContentType,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reon-file-id"] = fileId.ToString("D"),
            },
            cancellationToken);

        var upload = MultipartUpload.Create(
            Guid.CreateVersion7(),
            fileId,
            stored.StorageUploadId,
            _objectStorage.BucketName,
            objectKey,
            input.OriginalName,
            input.ContentType,
            input.Description,
            input.Tags,
            _options.DefaultOwner,
            now,
            expiresAt,
            retention is { } value ? checked((int)value.TotalSeconds) : null);

        await _uploadRepository.AddAsync(upload, cancellationToken);

        try
        {
            await _uploadRepository.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await TryAbortStorageAsync(upload, CancellationToken.None);
            throw;
        }

        LogInitiated(_logger, upload.Id, fileId, expiresAt);
        return upload;
    }

    public async Task<MultipartUploadPart> UploadPartAsync(
        Guid uploadId,
        int partNumber,
        Stream content,
        long contentLength,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var upload = await GetUploadAsync(uploadId, cancellationToken);
        var now = _clock.UtcNow;

        if (upload.ExpiresAt <= now)
        {
            await TryExpireAsync(upload, cancellationToken);
            throw new MultipartUploadExpiredException(uploadId);
        }

        if (!upload.IsActive)
        {
            throw new MultipartUploadNotActiveException(uploadId, upload.Status);
        }

        if (partNumber < 1 || partNumber > _options.MaxMultipartParts)
        {
            throw new FileValidationException(
                $"Part number must be between 1 and {_options.MaxMultipartParts}.");
        }

        if (contentLength <= 0 || contentLength > _options.MaxFileSizeBytes)
        {
            throw new FileValidationException("The part size is invalid.");
        }

        await using var tempFile = await _tempFiles.CreateAsync(content, _options.MaxFileSizeBytes, cancellationToken);
        if (tempFile.Size != contentLength)
        {
            throw new FileValidationException(
                "The request body size does not match Content-Length.");
        }

        await using var stream = tempFile.OpenRead();
        var storedPart = await _objectStorage.UploadPartAsync(
            upload.ObjectKey,
            upload.StorageUploadId,
            partNumber,
            stream,
            tempFile.Size,
            cancellationToken);

        var existingPart = await _uploadRepository.GetPartAsync(uploadId, partNumber, cancellationToken);
        if (existingPart is null)
        {
            _uploadRepository.AddPart(
                MultipartUploadPart.Create(
                    uploadId,
                    storedPart.PartNumber,
                    storedPart.ETag,
                    storedPart.Size,
                    now));
        }
        else
        {
            existingPart.Refresh(storedPart.ETag, storedPart.Size, now);
        }

        upload.MarkPartUploaded(now);
        await _uploadRepository.SaveChangesAsync(cancellationToken);

        LogPartUploaded(_logger, uploadId, partNumber, storedPart.Size);
        return existingPart ?? (await _uploadRepository.GetPartAsync(uploadId, partNumber, cancellationToken))!;
    }

    public async Task<FileMetadata> CompleteAsync(Guid uploadId, CancellationToken cancellationToken)
    {
        var upload = await GetUploadAsync(uploadId, cancellationToken);
        var now = _clock.UtcNow;

        if (upload.Status == MultipartUploadStatus.Completed)
        {
            var completedFile = await _fileRepository.GetByIdAsync(upload.FileId, cancellationToken);
            if (completedFile is not null)
            {
                return completedFile;
            }

            throw new MultipartUploadNotActiveException(upload.Id, upload.Status);
        }

        if (upload.Status == MultipartUploadStatus.Completing)
        {
            var existingFile = await _fileRepository.GetByIdAsync(upload.FileId, cancellationToken);
            if (existingFile is not null && existingFile.Status == FileStatus.Ready)
            {
                upload.MarkCompleted(now);
                await _uploadRepository.SaveChangesAsync(cancellationToken);
                return existingFile;
            }

            await TryAbortStorageAsync(upload, CancellationToken.None);
            upload.MarkFailed("Multipart upload completion did not finish.", now);
            await TrySaveUploadAsync(cancellationToken);
            throw new MultipartUploadNotActiveException(upload.Id, upload.Status);
        }

        if (upload.ExpiresAt <= now)
        {
            await TryExpireAsync(upload, cancellationToken);
            throw new MultipartUploadExpiredException(uploadId);
        }

        if (!upload.IsActive)
        {
            throw new MultipartUploadNotActiveException(upload.Id, upload.Status);
        }

        var parts = (await _uploadRepository.GetPartsAsync(uploadId, cancellationToken))
            .OrderBy(part => part.PartNumber)
            .ToArray();

        ValidateParts(parts);

        await _quotaGuard.EnsureCanWriteAsync(
            upload.Bucket,
            parts.Sum(part => part.Size),
            cancellationToken);

        upload.BeginComplete(now);
        await _uploadRepository.SaveChangesAsync(cancellationToken);

        StoredObject completed;
        try
        {
            completed = await _objectStorage.CompleteMultipartUploadAsync(
                upload.ObjectKey,
                upload.StorageUploadId,
                parts.Select(part => new MultipartPartETag(part.PartNumber, part.ETag)).ToArray(),
                cancellationToken);
        }
        catch (ObjectStorageException exception)
        {
            await TryAbortStorageAsync(upload, CancellationToken.None);
            upload.MarkFailed(Truncate(exception.Message, 1000), now);
            await TrySaveUploadAsync(cancellationToken);
            LogCompleteFailed(_logger, exception, uploadId);
            throw;
        }

        try
        {
            var info = await _objectStorage.HeadAsync(upload.ObjectKey, cancellationToken);
            var size = info?.Size ?? parts.Sum(part => part.Size);
            var sha256 = await ComputeSha256Async(upload.ObjectKey, cancellationToken);
            var retention = _options.ResolveRetention(upload.RetentionSeconds);
            var expiresAt = retention is { } value ? now + value : (DateTimeOffset?)null;

            var file = FileMetadata.CreatePending(
                upload.FileId,
                upload.Bucket,
                upload.ObjectKey,
                upload.OriginalName,
                upload.ContentType,
                size,
                sha256,
                upload.Owner,
                upload.Description,
                upload.Tags,
                version: 1,
                now: now,
                expiresAt: expiresAt);

            await _fileRepository.AddAsync(file, cancellationToken);
            await _fileRepository.SaveChangesAsync(cancellationToken);

            file.MarkReady(completed.ETag ?? info?.ETag, completed.VersionId ?? info?.VersionId, now);
            await _fileRepository.SaveChangesAsync(cancellationToken);

            upload.MarkCompleted(now);
            await TrySaveUploadAsync(cancellationToken);

            LogCompleted(_logger, uploadId, file.Id, size);
            return file;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            try
            {
                await _objectStorage.DeleteAsync(upload.ObjectKey, CancellationToken.None);
            }
            catch (Exception cleanupException)
            {
                LogCleanupFailed(_logger, cleanupException, upload.ObjectKey);
            }

            upload.MarkFailed(Truncate(exception.Message, 1000), now);
            await TrySaveUploadAsync(cancellationToken);
            throw;
        }
    }

    public async Task AbortAsync(Guid uploadId, CancellationToken cancellationToken)
    {
        var upload = await GetUploadAsync(uploadId, cancellationToken);

        if (upload.Status is MultipartUploadStatus.Aborted or MultipartUploadStatus.Expired)
        {
            return;
        }

        if (!upload.IsActive)
        {
            throw new MultipartUploadNotActiveException(upload.Id, upload.Status);
        }

        await _objectStorage.AbortMultipartUploadAsync(
            upload.ObjectKey,
            upload.StorageUploadId,
            cancellationToken);

        upload.MarkAborted(_clock.UtcNow);
        await _uploadRepository.SaveChangesAsync(cancellationToken);
        LogAborted(_logger, uploadId);
    }

    public async Task<(MultipartUpload Upload, IReadOnlyList<MultipartUploadPart> Parts)> GetAsync(
        Guid uploadId,
        CancellationToken cancellationToken)
    {
        var upload = await GetUploadAsync(uploadId, cancellationToken);
        var parts = await _uploadRepository.GetPartsAsync(uploadId, cancellationToken);
        return (upload, parts);
    }

    public Task<IReadOnlyList<MultipartUpload>> ListActiveAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        return _uploadRepository.GetActiveAsync(Math.Clamp(limit, 1, 200), cancellationToken);
    }

    public async Task CleanupExpiredAsync(CancellationToken cancellationToken)
    {
        var expired = await _uploadRepository.GetExpiredAsync(
            _clock.UtcNow,
            limit: 100,
            cancellationToken);

        foreach (var upload in expired)
        {
            try
            {
                await _objectStorage.AbortMultipartUploadAsync(
                    upload.ObjectKey,
                    upload.StorageUploadId,
                    cancellationToken);

                upload.MarkExpired(_clock.UtcNow);
                await _uploadRepository.SaveChangesAsync(cancellationToken);
                LogExpired(_logger, upload.Id);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogCleanupFailed(_logger, exception, upload.ObjectKey);
            }
        }
    }

    private void ValidateParts(MultipartUploadPart[] parts)
    {
        if (parts.Length == 0)
        {
            throw new FileValidationException("At least one part is required.");
        }

        for (var index = 0; index < parts.Length; index++)
        {
            var expectedPartNumber = index + 1;
            if (parts[index].PartNumber != expectedPartNumber)
            {
                throw new FileValidationException(
                    $"Part numbers must be sequential starting at 1; part {expectedPartNumber} is missing.");
            }

            if (index < parts.Length - 1 && parts[index].Size < _options.MinMultipartPartSizeBytes)
            {
                throw new FileValidationException(
                    $"All parts except the last must be at least {_options.MinMultipartPartSizeBytes} bytes.");
            }
        }
    }

    private async Task<string> ComputeSha256Async(string objectKey, CancellationToken cancellationToken)
    {
        await using var stream = await _objectStorage.OpenReadAsync(objectKey, cancellationToken)
            ?? throw new ObjectStorageException($"Object '{objectKey}' was not found after multipart completion.");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);

        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private async Task TryExpireAsync(MultipartUpload upload, CancellationToken cancellationToken)
    {
        try
        {
            await _objectStorage.AbortMultipartUploadAsync(
                upload.ObjectKey,
                upload.StorageUploadId,
                cancellationToken);

            upload.MarkExpired(_clock.UtcNow);
            await _uploadRepository.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            LogCleanupFailed(_logger, exception, upload.ObjectKey);
        }
    }

    private async Task TryAbortStorageAsync(MultipartUpload upload, CancellationToken cancellationToken)
    {
        try
        {
            await _objectStorage.AbortMultipartUploadAsync(
                upload.ObjectKey,
                upload.StorageUploadId,
                cancellationToken);
        }
        catch (Exception exception)
        {
            LogCleanupFailed(_logger, exception, upload.ObjectKey);
        }
    }

    private async Task TrySaveUploadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _uploadRepository.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            LogStatusPersistFailed(_logger, exception);
        }
    }

    private async Task<MultipartUpload> GetUploadAsync(Guid uploadId, CancellationToken cancellationToken)
    {
        return await _uploadRepository.GetByIdAsync(uploadId, cancellationToken)
            ?? throw new MultipartUploadNotFoundException(uploadId);
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "Multipart upload {UploadId} for file {FileId} initiated; expires at {ExpiresAt}.")]
    private static partial void LogInitiated(ILogger logger, Guid uploadId, Guid fileId, DateTimeOffset expiresAt);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Information, Message = "Part {PartNumber} uploaded for multipart upload {UploadId}: {Size} bytes.")]
    private static partial void LogPartUploaded(ILogger logger, Guid uploadId, int partNumber, long size);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Information, Message = "Multipart upload {UploadId} completed as file {FileId}: {Size} bytes.")]
    private static partial void LogCompleted(ILogger logger, Guid uploadId, Guid fileId, long size);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Information, Message = "Multipart upload {UploadId} aborted.")]
    private static partial void LogAborted(ILogger logger, Guid uploadId);

    [LoggerMessage(EventId = 3005, Level = LogLevel.Information, Message = "Expired multipart upload {UploadId} aborted.")]
    private static partial void LogExpired(ILogger logger, Guid uploadId);

    [LoggerMessage(EventId = 3006, Level = LogLevel.Error, Message = "S3 multipart completion failed for upload {UploadId}.")]
    private static partial void LogCompleteFailed(ILogger logger, Exception exception, Guid uploadId);

    [LoggerMessage(EventId = 3007, Level = LogLevel.Warning, Message = "Cleanup failed for object {ObjectKey}.")]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception, string objectKey);

    [LoggerMessage(EventId = 3008, Level = LogLevel.Error, Message = "Failed to persist multipart upload status.")]
    private static partial void LogStatusPersistFailed(ILogger logger, Exception exception);
}
