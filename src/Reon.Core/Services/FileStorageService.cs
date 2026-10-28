using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reon.Core.Entities;
using Reon.Core.Enums;
using Reon.Core.Exceptions;
using Reon.Core.Interfaces;
using Reon.Core.Models;
using Reon.Core.Options;

namespace Reon.Core.Services;

/// <summary>
/// Application service for the File Storage API. It owns the upload/read/delete
/// saga and the metadata lifecycle. Infrastructure details stay behind
/// <see cref="IObjectStorage"/> and <see cref="IFileMetadataRepository"/>.
/// </summary>
public sealed partial class FileStorageService
{
    private readonly IObjectStorage _objectStorage;
    private readonly IFileMetadataRepository _repository;
    private readonly ITempFileFactory _tempFiles;
    private readonly IClock _clock;
    private readonly FileStorageOptions _options;
    private readonly ILogger<FileStorageService> _logger;

    public FileStorageService(
        IObjectStorage objectStorage,
        IFileMetadataRepository repository,
        ITempFileFactory tempFiles,
        IClock clock,
        IOptions<FileStorageOptions> options,
        ILogger<FileStorageService> logger)
    {
        _objectStorage = objectStorage;
        _repository = repository;
        _tempFiles = tempFiles;
        _clock = clock;
        _options = options.Value;
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

        var fileId = Guid.CreateVersion7();
        var objectKey = $"{_options.KeyPrefix}{fileId:N}";

        // Spooling gives us a seekable stream (S3 retries and multipart need it),
        // a known size, and a checksum before any metadata becomes visible.
        await using var tempFile = await _tempFiles.CreateAsync(content, _options.MaxFileSizeBytes, cancellationToken);

        var now = _clock.UtcNow;
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
            now: now);

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
            // The object may already exist. Best-effort remove it so a failed
            // request does not leave an untracked object behind.
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

    public async Task<FileContent> OpenContentAsync(
        Guid fileId,
        CancellationToken cancellationToken)
    {
        var file = await GetExistingFileAsync(fileId, cancellationToken);

        if (file.Status != FileStatus.Ready)
        {
            throw new FileNotReadyException(file.Id, file.Status);
        }

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

    public async Task<DeleteOutcome> DeleteAsync(
        Guid fileId,
        CancellationToken cancellationToken)
    {
        var file = await _repository.GetByIdAsync(fileId, cancellationToken);
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
            // Idempotent by contract: a missing object is not an error.
            await _objectStorage.DeleteAsync(file.ObjectKey, cancellationToken);

            file.MarkDeleted(_clock.UtcNow);
            await _repository.SaveChangesAsync(cancellationToken);

            LogFileDeleted(_logger, file.Id);
            return DeleteOutcome.Deleted;
        }
        catch (ObjectStorageException exception)
        {
            // The row stays in Deleting and the reconciliation worker retries.
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
