namespace Reon.Core.Services;

/// <summary>
/// Прикладной сервис API файлового хранилища. Он владеет сагой загрузки,
/// чтения и удаления, а также жизненным циклом метаданных. Детали
/// инфраструктуры остаются за <see cref="IObjectStorage"/> и <see cref="IFileMetadataRepository"/>.
/// </summary>
public sealed partial class FileStorageService
{
    private readonly IObjectStorage _objectStorage;
    private readonly IFileMetadataRepository _repository;
    private readonly ITempFileFactory _tempFiles;
    private readonly IClock _clock;
    private readonly FileStorageOptions _options;
    private readonly IStorageQuotaGuard _quotaGuard;
    private readonly ILogger<FileStorageService> _logger;

    /// <summary>
    /// Создаёт сервис файлового домена.
    /// </summary>
    /// <param name="objectStorage">Операции с объектами в S3.</param>
    /// <param name="repository">Строки метаданных файлов в Postgres.</param>
    /// <param name="tempFiles">Буферизация потока, проверка размера и SHA-256.</param>
    /// <param name="clock">Единый источник времени для статусов и меток.</param>
    /// <param name="options">Лимиты загрузки, ключей и фоновой очистки.</param>
    /// <param name="quotaGuard">Проверка квоты бакета перед записью.</param>
    /// <param name="logger">Лог операций и ошибок загрузки.</param>
    public FileStorageService(
        IObjectStorage objectStorage,
        IFileMetadataRepository repository,
        ITempFileFactory tempFiles,
        IClock clock,
        IOptions<FileStorageOptions> options,
        IStorageQuotaGuard quotaGuard,
        ILogger<FileStorageService> logger)
    {
        _objectStorage = objectStorage;
        _repository = repository;
        _tempFiles = tempFiles;
        _clock = clock;
        _options = options.Value;
        _quotaGuard = quotaGuard;
        _logger = logger;
    }

    public async Task<FileMetadata> UploadAsync(
        Stream content,
        UploadFileCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(command);

        var originalName = NormalizeOriginalName(command.OriginalName);
        var contentType = NormalizeContentType(command.ContentType);
        var description = NormalizeDescription(command.Description);
        var tags = NormalizeTags(command.Tags);
        var retention = _options.ResolveRetention(command.RetentionSeconds);

        var fileId = Guid.CreateVersion7();
        var objectKey = $"{_options.KeyPrefix}{fileId:N}{GetSafeExtension(originalName)}";

        // Буферизация даёт поток с произвольным доступом, известный размер и
        // контрольную сумму до появления метаданных. Произвольный доступ нужен
        // для повторов S3 и параллельной загрузки файла по частям (multipart-загрузки).
        await using var tempFile = await _tempFiles.CreateAsync(content, _options.MaxFileSizeBytes, cancellationToken);

        // Квота проверяется до появления строки метаданных, поэтому отклонённая
        // загрузка ничего не оставляет после себя.
        await _quotaGuard.EnsureCanWriteAsync(_objectStorage.BucketName, tempFile.Size, cancellationToken);

        var now = _clock.UtcNow;
        var expiresAt = retention is { } lifetime ? now + lifetime : (DateTimeOffset?)null;
        var file = FileMetadata.CreatePending(
            fileId,
            bucket: _objectStorage.BucketName,
            objectKey: objectKey,
            originalName: originalName,
            contentType: contentType,
            size: tempFile.Size,
            sha256: tempFile.Sha256,
            owner: _options.DefaultOwner,
            description: description,
            tags: tags,
            version: 1,
            now: now,
            expiresAt: expiresAt);

        await _repository.AddAsync(file, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);

        try
        {
            await using var stream = tempFile.OpenRead();
            var stored = await _objectStorage.PutAsync(
                new ObjectStoragePutRequest(
                    objectKey,
                    contentType,
                    tempFile.Size,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["reon-file-id"] = fileId.ToString("D"),
                        ["reon-sha256"] = tempFile.Sha256,
                    }),
                stream,
                cancellationToken);

            file.MarkReady(stored.ETag, stored.VersionId, _clock.UtcNow);
            await _repository.SaveChangesAsync(cancellationToken);

            LogFileUploaded(_logger, file.Id, file.Size, file.Owner);

            return file;
        }
        catch (ObjectStorageException exception)
        {
            file.MarkFailed(Truncate(exception.Message, 1000), _clock.UtcNow);
            await TrySaveAsync(cancellationToken);

            LogUploadFailed(_logger, exception, fileId);
            throw;
        }
        catch (Exception)
        {
            // Объект мог уже существовать. Пытаемся удалить его, чтобы неудачный
            // запрос не оставил объект без учёта.
            try
            {
                await _objectStorage.DeleteAsync(objectKey, CancellationToken.None);
            }
            catch (Exception cleanupException)
            {
                LogCleanupFailed(_logger, cleanupException, objectKey);
            }

            throw;
        }
    }

    public async Task<FileMetadata> GetMetadataAsync(
        Guid fileId,
        CancellationToken cancellationToken)
    {
        return await GetExistingFileAsync(fileId, cancellationToken);
    }

    /// <summary>
    /// Возвращает описание только для доступного файла: просроченный или
    /// неготовый файл не должен отдавать заголовки содержимого.
    /// </summary>
    public async Task<FileMetadata> GetAvailableMetadataAsync(
        Guid fileId,
        CancellationToken cancellationToken)
    {
        var file = await GetExistingFileAsync(fileId, cancellationToken);
        EnsureAvailable(file);

        return file;
    }

    public async Task<FileContent> OpenContentAsync(
        Guid fileId,
        CancellationToken cancellationToken)
    {
        var file = await GetExistingFileAsync(fileId, cancellationToken);
        EnsureAvailable(file);

        var stream = await _objectStorage.OpenReadAsync(file.ObjectKey, cancellationToken);
        if (stream is null)
        {
            file.MarkMissing("The object is missing from the object store.", _clock.UtcNow);
            await TrySaveAsync(cancellationToken);

            LogObjectMissing(_logger, file.ObjectKey, file.Id);

            throw new FileMetadataNotFoundException(file.Id);
        }

        return new FileContent(stream, file.OriginalName, file.ContentType, file.Size, file.ETag);
    }

    public Task<FileListResult> ListAsync(
        FileListQuery query,
        CancellationToken cancellationToken)
    {
        return _repository.ListAsync(query, cancellationToken);
    }

    public Task<StorageStats> GetStatsAsync(CancellationToken cancellationToken)
    {
        return _repository.GetStatsAsync(cancellationToken);
    }

    public async Task<FileMetadata> UpdateMetadataAsync(
        Guid fileId,
        string? originalName,
        string? description,
        IReadOnlyDictionary<string, string>? tags,
        CancellationToken cancellationToken)
    {
        var file = await GetExistingFileAsync(fileId, cancellationToken);
        EnsureAvailable(file);

        var normalizedName = originalName is null
            ? file.OriginalName
            : FileInput.NormalizeOriginalName(originalName, _options);

        var normalizedDescription = description is null
            ? file.Description
            : FileInput.NormalizeDescription(description, _options);

        var normalizedTags = tags is null
            ? file.Tags
            : FileInput.NormalizeTags(tags, _options);

        file.UpdateMetadata(normalizedName, normalizedDescription, normalizedTags, _clock.UtcNow);
        await _repository.SaveChangesAsync(cancellationToken);

        return file;
    }

    public async Task<FileMetadata> CopyAsync(
        Guid sourceFileId,
        CopyFileCommand command,
        CancellationToken cancellationToken)
    {
        var source = await GetExistingFileAsync(sourceFileId, cancellationToken);
        EnsureAvailable(source);

        var retention = _options.ResolveRetention(command.RetentionSeconds);

        var originalName = command.OriginalName is null
            ? source.OriginalName
            : FileInput.NormalizeOriginalName(command.OriginalName, _options);

        var description = command.Description is null
            ? source.Description
            : FileInput.NormalizeDescription(command.Description, _options);

        var tags = command.Tags is null
            ? source.Tags
            : FileInput.NormalizeTags(command.Tags, _options);

        var fileId = Guid.CreateVersion7();
        var objectKey = FileInput.BuildObjectKey(fileId, originalName, _options);
        var now = _clock.UtcNow;
        var expiresAt = retention is { } lifetime ? now + lifetime : (DateTimeOffset?)null;

        var stored = await _objectStorage.CopyAsync(
            source.ObjectKey,
            objectKey,
            source.ContentType,
            source.Size,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reon-file-id"] = fileId.ToString("D"),
                ["reon-sha256"] = source.Sha256,
            },
            cancellationToken);

        var copy = FileMetadata.CreatePending(
            fileId,
            source.Bucket,
            objectKey,
            originalName,
            source.ContentType,
            source.Size,
            source.Sha256,
            _options.DefaultOwner,
            description,
            tags,
            version: 1,
            now: now,
            expiresAt: expiresAt);

        try
        {
            await _repository.AddAsync(copy, cancellationToken);
            await _repository.SaveChangesAsync(cancellationToken);

            copy.MarkReady(stored.ETag, stored.VersionId, _clock.UtcNow);
            await _repository.SaveChangesAsync(cancellationToken);

            return copy;
        }
        catch
        {
            try
            {
                await _objectStorage.DeleteAsync(objectKey, CancellationToken.None);
            }
            catch (Exception cleanupException)
            {
                LogCleanupFailed(_logger, cleanupException, objectKey);
            }

            throw;
        }
    }

    public async Task<DeleteOutcome> DeleteAsync(
        Guid fileId,
        CancellationToken cancellationToken)
    {        var file = await _repository.GetByIdAsync(fileId, cancellationToken);
        if (file is null)
        {
            throw new FileMetadataNotFoundException(fileId);
        }

        if (file.Status == FileStatus.Deleted)
        {
            return DeleteOutcome.AlreadyDeleted;
        }

        if (file.Status == FileStatus.Deleting)
        {
            return DeleteOutcome.InProgress;
        }

        file.BeginDelete(_clock.UtcNow);
        await _repository.SaveChangesAsync(cancellationToken);

        try
        {
            // По контракту идемпотентно: отсутствующий объект не считается ошибкой.
            await _objectStorage.DeleteAsync(file.ObjectKey, cancellationToken);

            file.MarkDeleted(_clock.UtcNow);
            await _repository.SaveChangesAsync(cancellationToken);

            LogFileDeleted(_logger, file.Id);
            return DeleteOutcome.Deleted;
        }
        catch (ObjectStorageException exception)
        {
            // Строка остаётся в Deleting, и фоновая сверка повторит попытку.
            LogDeleteFailed(_logger, exception, fileId);
            throw;
        }
    }

    private async Task<FileMetadata> GetExistingFileAsync(
        Guid fileId,
        CancellationToken cancellationToken)
    {
        var file = await _repository.GetByIdAsync(fileId, cancellationToken);
        if (file is null || file.Status == FileStatus.Deleted)
        {
            throw new FileMetadataNotFoundException(fileId);
        }

        return file;
    }

    private void EnsureAvailable(FileMetadata file)
    {
        if (file.Status == FileStatus.Expired || file.IsExpired(_clock.UtcNow))
        {
            throw new FileExpiredException(file.Id, file.ExpiresAt);
        }

        if (file.Status != FileStatus.Ready)
        {
            throw new FileNotReadyException(file.Id, file.Status);
        }
    }

    private static string GetSafeExtension(string originalName)
    {
        var extension = Path.GetExtension(originalName);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 16)
        {
            return string.Empty;
        }

        var candidate = extension.ToLowerInvariant();
        return candidate[1..].All(char.IsLetterOrDigit) ? candidate : string.Empty;
    }

    private string NormalizeOriginalName(string? value)
    {
        var candidate = (value ?? string.Empty).Replace('\\', '/');
        candidate = Path.GetFileName(candidate);
        candidate = new string(candidate.Where(character => !char.IsControl(character)).ToArray()).Trim();

        if (candidate.Length == 0)
        {
            candidate = "file";
        }

        if (candidate.Length > _options.MaxOriginalNameLength)
        {
            candidate = candidate[.._options.MaxOriginalNameLength];
        }

        return candidate;
    }

    private string NormalizeContentType(string? value)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? "application/octet-stream" : value.Trim();

        if (candidate.Length > _options.MaxContentTypeLength ||
            candidate.Contains('\r') ||
            candidate.Contains('\n'))
        {
            throw new FileValidationException("The content type is invalid.");
        }

        return candidate;
    }

    private string? NormalizeDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var candidate = value.Trim();
        if (candidate.Length > _options.MaxDescriptionLength)
        {
            throw new FileValidationException(
                $"The description must not exceed {_options.MaxDescriptionLength} characters.");
        }

        return candidate;
    }

    private JsonDocument? NormalizeTags(IReadOnlyDictionary<string, string>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return null;
        }

        if (tags.Count > _options.MaxTags)
        {
            throw new FileValidationException($"At most {_options.MaxTags} tags are allowed.");
        }

        foreach (var (key, value) in tags)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > _options.MaxTagKeyLength)
            {
                throw new FileValidationException(
                    $"A tag key must be non-empty and at most {_options.MaxTagKeyLength} characters.");
            }

            if (value is null || value.Length > _options.MaxTagValueLength)
            {
                throw new FileValidationException(
                    $"A tag value must be non-null and at most {_options.MaxTagValueLength} characters.");
            }
        }

        return JsonSerializer.SerializeToDocument(tags);
    }

    private async Task TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _repository.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            LogStatusPersistFailed(_logger, exception);
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "File {FileId} uploaded: {Size} bytes, owner {Owner}.")]
    private static partial void LogFileUploaded(ILogger logger, Guid fileId, long size, string owner);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Error, Message = "Object storage upload failed for file {FileId}.")]
    private static partial void LogUploadFailed(ILogger logger, Exception exception, Guid fileId);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning, Message = "Failed to clean up object {ObjectKey} after an upload failure.")]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception, string objectKey);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Error, Message = "Object {ObjectKey} for file {FileId} is missing from storage.")]
    private static partial void LogObjectMissing(ILogger logger, string objectKey, Guid fileId);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Information, Message = "File {FileId} deleted.")]
    private static partial void LogFileDeleted(ILogger logger, Guid fileId);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Error, Message = "Object storage delete failed for file {FileId}.")]
    private static partial void LogDeleteFailed(ILogger logger, Exception exception, Guid fileId);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Error, Message = "Failed to persist a file status transition.")]
    private static partial void LogStatusPersistFailed(ILogger logger, Exception exception);
}
