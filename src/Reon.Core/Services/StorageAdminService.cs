using System.Text.RegularExpressions;

namespace Reon.Core.Services;

/// <summary>
/// Административные сценарии объектного хранилища: бакеты, сырые объекты,
/// квоты, доступ, lifecycle и журнал операций. Сервис отвечает за валидацию
/// и аудит; особенности провайдера остаются за <see cref="IObjectStorageAdmin"/>.
/// </summary>
public sealed partial class StorageAdminService : IStorageAdminService
{
    private const int StatusOk = 200;
    private const int StatusCreated = 201;
    private const int StatusNoContent = 204;

    private static readonly Regex BucketNamePattern = new(
        "^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex IpAddressPattern = new(
        @"^(\d{1,3}\.){3}\d{1,3}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IObjectStorageAdmin _storage;
    private readonly IBucketSettingsRepository _settings;
    private readonly IAuditRepository _audit;
    private readonly IStorageAuditor _auditor;
    private readonly IStorageQuotaGuard _quotaGuard;
    private readonly IStorageEventPublisher _events;
    private readonly IClock _clock;
    private readonly FileStorageOptions _fileOptions;
    private readonly AdminOptions _adminOptions;
    private readonly ILogger<StorageAdminService> _logger;

    /// <summary>
    /// Создаёт сервис административных сценариев.
    /// </summary>
    /// <param name="storage">Административные операции S3 по всем бакетам.</param>
    /// <param name="settings">Квоты и режим доступа бакетов в Postgres.</param>
    /// <param name="audit">Чтение журнала операций.</param>
    /// <param name="auditor">Запись операций в журнал.</param>
    /// <param name="quotaGuard">Проверка квоты перед загрузкой объекта.</param>
    /// <param name="events">Публикация событий WebSocket-подписчикам.</param>
    /// <param name="clock">Время для аудита, usage и presigned URL.</param>
    /// <param name="fileOptions">Лимиты тегов и метаданных, общие с файловым API.</param>
    /// <param name="adminOptions">Кеш usage, лимиты страниц и срок presigned URL.</param>
    /// <param name="logger">Лог откатов и ошибок фоновых операций.</param>
    public StorageAdminService(
        IObjectStorageAdmin storage,
        IBucketSettingsRepository settings,
        IAuditRepository audit,
        IStorageAuditor auditor,
        IStorageQuotaGuard quotaGuard,
        IStorageEventPublisher events,
        IClock clock,
        IOptions<FileStorageOptions> fileOptions,
        IOptions<AdminOptions> adminOptions,
        ILogger<StorageAdminService> logger)
    {
        _storage = storage;
        _settings = settings;
        _audit = audit;
        _auditor = auditor;
        _quotaGuard = quotaGuard;
        _events = events;
        _clock = clock;
        _fileOptions = fileOptions.Value;
        _adminOptions = adminOptions.Value;
        _logger = logger;
    }

    public async Task<StorageOverview> GetOverviewAsync(
        bool refreshUsage,
        CancellationToken cancellationToken)
    {
        var buckets = await _storage.ListBucketsAsync(cancellationToken);
        var settings = await LoadSettingsAsync(cancellationToken);

        var summaries = new List<BucketSummary>(buckets.Count);

        foreach (var bucket in buckets)
        {
            var bucketSettings = await EnsureSettingsAsync(bucket, settings, cancellationToken);
            summaries.Add(await BuildSummaryAsync(bucket, bucketSettings, refreshUsage, cancellationToken));
        }

        return new StorageOverview(
            summaries.Count,
            summaries.Sum(summary => summary.Usage.ObjectCount),
            summaries.Sum(summary => summary.Usage.UsedBytes),
            summaries.Count(summary => summary.AccessMode == BucketAccessMode.PublicRead),
            summaries
                .OrderByDescending(summary => summary.Usage.UsedBytes)
                .ThenBy(summary => summary.Name, StringComparer.Ordinal)
                .ToArray(),
            _clock.UtcNow);
    }

    public async Task<BucketListResult> ListBucketsAsync(
        BucketListQuery query,
        CancellationToken cancellationToken)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, _adminOptions.MaxPageSize);

        var buckets = await _storage.ListBucketsAsync(cancellationToken);
        var settings = await LoadSettingsAsync(cancellationToken);

        var summaries = new List<BucketSummary>(buckets.Count);

        foreach (var bucket in buckets)
        {
            var bucketSettings = await EnsureSettingsAsync(bucket, settings, cancellationToken);
            summaries.Add(await BuildSummaryAsync(bucket, bucketSettings, refreshUsage: false, cancellationToken));
        }

        IEnumerable<BucketSummary> filtered = summaries;

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            filtered = filtered.Where(summary =>
                summary.Name.Contains(query.Search.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        filtered = (query.Sort?.ToLowerInvariant()) switch
        {
            "size" => filtered
                .OrderByDescending(summary => summary.Usage.UsedBytes)
                .ThenBy(summary => summary.Name, StringComparer.Ordinal),
            "objects" => filtered
                .OrderByDescending(summary => summary.Usage.ObjectCount)
                .ThenBy(summary => summary.Name, StringComparer.Ordinal),
            _ => filtered.OrderBy(summary => summary.Name, StringComparer.Ordinal),
        };

        var items = filtered.ToArray();
        var pageItems = items
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArray();

        return new BucketListResult(pageItems, items.Length, page, pageSize);
    }

    public async Task<BucketSummary> GetBucketAsync(
        string bucket,
        bool refreshUsage,
        CancellationToken cancellationToken)
    {
        var storageBucket = await GetStorageBucketAsync(bucket, cancellationToken);
        var settings = await EnsureSettingsAsync(storageBucket, cancellationToken);

        return await BuildSummaryAsync(storageBucket, settings, refreshUsage, cancellationToken);
    }

    public async Task<BucketSummary> CreateBucketAsync(
        CreateBucketCommand command,
        string actor,
        CancellationToken cancellationToken)
    {
        var bucket = ValidateBucketName(command.Name);

        if (command.QuotaBytes is <= 0)
        {
            throw new StorageValidationException("Quota must be a positive number of bytes.");
        }

        try
        {
            var existing = await _storage.GetBucketAsync(bucket, cancellationToken);
            if (existing is not null)
            {
                throw new BucketAlreadyExistsException(bucket);
            }

            await _storage.CreateBucketAsync(
                bucket,
                command.VersioningEnabled || command.ObjectLockEnabled,
                command.ObjectLockEnabled,
                cancellationToken);

            var now = _clock.UtcNow;
            var settings = BucketSettings.Create(bucket, command.QuotaBytes, now);
            await _settings.AddAsync(settings, cancellationToken);
            await _settings.SaveChangesAsync(cancellationToken);

            await RecordAuditAsync(
                actor,
                "bucket.create",
                bucket,
                AuditResult.Success,
                StatusCreated,
                new { command.VersioningEnabled, command.ObjectLockEnabled, command.QuotaBytes },
                cancellationToken);

            await PublishAsync(
                StorageEvent.BucketChanged,
                new { bucket, change = "created" },
                bucket,
                cancellationToken);

            return await BuildSummaryAsync(
                new StorageBucket(bucket, now),
                settings,
                refreshUsage: false,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RecordFailureAsync(actor, "bucket.create", bucket, exception, cancellationToken);
            throw;
        }
    }

    public async Task<BucketSummary> UpdateBucketAsync(
        string bucket,
        UpdateBucketCommand command,
        string actor,
        CancellationToken cancellationToken)
    {
        ValidateBucketName(bucket);

        if (command.RemoveQuota && command.QuotaBytes is not null)
        {
            throw new StorageValidationException("QuotaBytes and RemoveQuota cannot be used together.");
        }

        if (command.QuotaBytes is <= 0)
        {
            throw new StorageValidationException("Quota must be a positive number of bytes.");
        }

        try
        {
            var storageBucket = await GetStorageBucketAsync(bucket, cancellationToken);
            var settings = await EnsureSettingsAsync(storageBucket, cancellationToken);
            var now = _clock.UtcNow;

            if (command.VersioningEnabled is { } versioningEnabled)
            {
                var current = await _storage.GetVersioningAsync(bucket, cancellationToken);
                if (current.Enabled != versioningEnabled)
                {
                    await _storage.SetVersioningAsync(bucket, versioningEnabled, cancellationToken);
                }
            }

            if (command.RemoveQuota)
            {
                settings.SetQuota(null, now);
            }
            else if (command.QuotaBytes is { } quotaBytes)
            {
                settings.SetQuota(quotaBytes, now);
            }

            await _settings.SaveChangesAsync(cancellationToken);

            await RecordAuditAsync(
                actor,
                "bucket.update",
                bucket,
                AuditResult.Success,
                StatusOk,
                new { command.VersioningEnabled, command.QuotaBytes, command.RemoveQuota },
                cancellationToken);

            await PublishAsync(
                StorageEvent.BucketChanged,
                new { bucket, change = "updated" },
                bucket,
                cancellationToken);

            return await BuildSummaryAsync(storageBucket, settings, refreshUsage: false, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RecordFailureAsync(actor, "bucket.update", bucket, exception, cancellationToken);
            throw;
        }
    }

    public async Task<BucketAccess> GetBucketAccessAsync(
        string bucket,
        CancellationToken cancellationToken)
    {
        var storageBucket = await GetStorageBucketAsync(bucket, cancellationToken);
        var settings = await EnsureSettingsAsync(storageBucket, cancellationToken);
        var policy = await _storage.GetBucketPolicyAsync(bucket, cancellationToken);

        return new BucketAccess(settings.AccessMode, policy ?? string.Empty);
    }

    public async Task<BucketAccess> SetBucketAccessAsync(
        string bucket,
        BucketAccessMode mode,
        string actor,
        CancellationToken cancellationToken)
    {
        ValidateBucketName(bucket);

        try
        {
            var storageBucket = await GetStorageBucketAsync(bucket, cancellationToken);
            var settings = await EnsureSettingsAsync(storageBucket, cancellationToken);

            var policy = mode == BucketAccessMode.PublicRead
                ? BuildPublicReadPolicy(bucket)
                : null;

            await _storage.SetBucketPolicyAsync(bucket, policy, cancellationToken);

            settings.SetAccessMode(mode, _clock.UtcNow);
            await _settings.SaveChangesAsync(cancellationToken);

            await RecordAuditAsync(
                actor,
                "bucket.access.update",
                bucket,
                AuditResult.Success,
                StatusOk,
                new { mode = mode.ToString() },
                cancellationToken);

            await PublishAsync(
                StorageEvent.BucketChanged,
                new { bucket, change = "access", mode = mode.ToString() },
                bucket,
                cancellationToken);

            return new BucketAccess(mode, policy ?? string.Empty);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RecordFailureAsync(actor, "bucket.access.update", bucket, exception, cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<LifecycleRule>> GetLifecycleAsync(
        string bucket,
        CancellationToken cancellationToken)
    {
        await GetStorageBucketAsync(bucket, cancellationToken);
        return await _storage.GetLifecycleAsync(bucket, cancellationToken);
    }

    public async Task<IReadOnlyList<LifecycleRule>> SetLifecycleAsync(
        string bucket,
        IReadOnlyList<LifecycleRule> rules,
        string actor,
        CancellationToken cancellationToken)
    {
        ValidateBucketName(bucket);
        ValidateLifecycleRules(rules);

        try
        {
            await GetStorageBucketAsync(bucket, cancellationToken);
            await _storage.SetLifecycleAsync(bucket, rules, cancellationToken);

            await RecordAuditAsync(
                actor,
                "bucket.lifecycle.update",
                bucket,
                AuditResult.Success,
                StatusOk,
                new { rules = rules.Count },
                cancellationToken);

            await PublishAsync(
                StorageEvent.BucketChanged,
                new { bucket, change = "lifecycle" },
                bucket,
                cancellationToken);

            return rules;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RecordFailureAsync(actor, "bucket.lifecycle.update", bucket, exception, cancellationToken);
            throw;
        }
    }

    public async Task<BucketUsage> GetUsageAsync(
        string bucket,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var storageBucket = await GetStorageBucketAsync(bucket, cancellationToken);
        var settings = await EnsureSettingsAsync(storageBucket, cancellationToken);
        var stats = await _storage.GetUsageAsync(bucket, refresh, cancellationToken);

        return ToUsage(stats, settings.QuotaBytes);
    }

    public async Task<BucketUsage> RefreshUsageAsync(
        string bucket,
        string actor,
        CancellationToken cancellationToken)
    {
        ValidateBucketName(bucket);

        try
        {
            var storageBucket = await GetStorageBucketAsync(bucket, cancellationToken);
            var settings = await EnsureSettingsAsync(storageBucket, cancellationToken);
            var stats = await _storage.GetUsageAsync(bucket, refresh: true, cancellationToken);
            var usage = ToUsage(stats, settings.QuotaBytes);

            await RecordAuditAsync(
                actor,
                "usage.refresh",
                bucket,
                AuditResult.Success,
                StatusOk,
                new { usage.UsedBytes, usage.ObjectCount },
                cancellationToken);

            await PublishAsync(StorageEvent.UsageUpdated, usage, bucket, cancellationToken);

            return usage;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RecordFailureAsync(actor, "usage.refresh", bucket, exception, cancellationToken);
            throw;
        }
    }

    public async Task<ObjectListing> ListObjectsAsync(
        ObjectListQuery query,
        CancellationToken cancellationToken)
    {
        await GetStorageBucketAsync(query.Bucket, cancellationToken);

        var prefix = NormalizePrefix(query.Prefix);
        var maxKeys = Math.Clamp(query.MaxKeys, 1, 1000);
        var delimiter = string.IsNullOrEmpty(query.Delimiter) ? "/" : query.Delimiter;

        var listing = string.IsNullOrWhiteSpace(query.Search)
            ? await _storage.ListObjectsAsync(
                query.Bucket,
                prefix,
                delimiter,
                maxKeys,
                query.ContinuationToken,
                cancellationToken)
            : await SearchObjectsAsync(query, prefix, delimiter, maxKeys, cancellationToken);

        if (!query.IncludeContentType || listing.Objects.Count == 0)
        {
            return listing;
        }

        return listing with
        {
            Objects = await EnrichContentTypesAsync(query.Bucket, listing.Objects, cancellationToken),
        };
    }

    public async Task<ObjectInfo> GetObjectInfoAsync(
        string bucket,
        string key,
        string? versionId,
        CancellationToken cancellationToken)
    {
        await GetStorageBucketAsync(bucket, cancellationToken);
        ValidateObjectKey(key);

        var info = await _storage.HeadObjectAsync(bucket, key, versionId, cancellationToken)
            ?? throw new StoredObjectNotFoundException(bucket, key);

        return info;
    }

    public async Task<ObjectContent> OpenObjectAsync(
        string bucket,
        string key,
        string? versionId,
        CancellationToken cancellationToken)
    {
        var info = await GetObjectInfoAsync(bucket, key, versionId, cancellationToken);

        var stream = await _storage.OpenReadAsync(bucket, key, versionId, cancellationToken)
            ?? throw new StoredObjectNotFoundException(bucket, key);

        return new ObjectContent(
            stream,
            info.ContentType ?? "application/octet-stream",
            info.Size,
            info.ETag,
            info.VersionId);
    }

    public async Task<IReadOnlyList<ObjectVersion>> ListVersionsAsync(
        string bucket,
        string key,
        CancellationToken cancellationToken)
    {
        await GetStorageBucketAsync(bucket, cancellationToken);
        ValidateObjectKey(key);

        return await _storage.ListVersionsAsync(bucket, key, key, cancellationToken);
    }

    public async Task DeleteVersionAsync(
        string bucket,
        string key,
        string versionId,
        string actor,
        CancellationToken cancellationToken)
    {
        ValidateBucketName(bucket);
        ValidateObjectKey(key);

        if (string.IsNullOrWhiteSpace(versionId))
        {
            throw new StorageValidationException("VersionId is required.");
        }

        try
        {
            await GetStorageBucketAsync(bucket, cancellationToken);
            await _storage.DeleteObjectAsync(bucket, key, versionId, cancellationToken);

            await RecordAuditAsync(
                actor,
                "object.version.delete",
                $"{bucket}/{key}",
                AuditResult.Success,
                StatusNoContent,
                new { versionId },
                cancellationToken);

            await PublishAsync(
                StorageEvent.ObjectChanged,
                new { bucket, key, change = "version-deleted", versionId },
                bucket,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RecordFailureAsync(actor, "object.version.delete", $"{bucket}/{key}", exception, cancellationToken);
            throw;
        }
    }

    public async Task<ObjectInfo> UploadObjectAsync(
        UploadObjectCommand command,
        Stream content,
        string actor,
        CancellationToken cancellationToken)
    {
        ValidateBucketName(command.Bucket);
        ValidateObjectKey(command.Key);

        if (command.Size < 0)
        {
            throw new StorageValidationException("Object size must not be negative.");
        }

        if (command.Size > _fileOptions.MaxFileSizeBytes)
        {
            throw new FileTooLargeException(_fileOptions.MaxFileSizeBytes);
        }

        try
        {
            await GetStorageBucketAsync(command.Bucket, cancellationToken);
            await _quotaGuard.EnsureCanWriteAsync(command.Bucket, command.Size, cancellationToken);

            var stored = await _storage.PutObjectAsync(
                command.Bucket,
                command.Key,
                command.ContentType,
                command.Size,
                content,
                command.Metadata,
                cancellationToken);

            if (command.Tags is { Count: > 0 })
            {
                try
                {
                    await _storage.SetTagsAsync(command.Bucket, command.Key, command.Tags, cancellationToken);
                }
                catch (Exception exception) when (
                    exception is StorageCapabilityNotSupportedException or ObjectStorageException)
                {
                    // Не оставлять объект, если набор тегов вызывающего
                    // применить не удалось: запрос считается неуспешным.
                    await TryDeleteUploadedObjectAsync(command.Bucket, command.Key, cancellationToken);
                    throw;
                }
            }

            await RecordAuditAsync(
                actor,
                "object.upload",
                $"{command.Bucket}/{command.Key}",
                AuditResult.Success,
                StatusCreated,
                new { command.Size, command.ContentType },
                cancellationToken);

            await PublishAsync(
                StorageEvent.ObjectChanged,
                new { bucket = command.Bucket, key = command.Key, change = "created", stored.Size },
                command.Bucket,
                cancellationToken);

            return stored;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RecordFailureAsync(actor, "object.upload", $"{command.Bucket}/{command.Key}", exception, cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<ObjectInfo>> UploadBatchAsync(
        string bucket,
        IReadOnlyList<BatchUploadEntry> entries,
        IReadOnlyDictionary<string, string>? metadata,
        IReadOnlyDictionary<string, string>? tags,
        string actor,
        CancellationToken cancellationToken)
    {
        ValidateBucketName(bucket);

        if (entries is null || entries.Count == 0)
        {
            throw new StorageValidationException("Provide at least one file to upload.");
        }

        var uploadedKeys = new List<string>(entries.Count);

        try
        {
            var result = new List<ObjectInfo>(entries.Count);

            foreach (var entry in entries)
            {
                await using var stream = await entry.OpenStream(cancellationToken);

                var info = await UploadObjectAsync(
                    new UploadObjectCommand(bucket, entry.Key, entry.ContentType, entry.Size, metadata, tags),
                    stream,
                    actor,
                    cancellationToken);

                result.Add(info);
                uploadedKeys.Add(entry.Key);
            }

            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Батч — одна логическая операция: откатываем уже записанные
            // этим вызовом объекты перед тем, как сообщить об ошибке.
            foreach (var key in uploadedKeys)
            {
                try
                {
                    await _storage.DeleteObjectAsync(bucket, key, versionId: null, CancellationToken.None);
                }
                catch (Exception rollbackException) when (rollbackException is not OperationCanceledException)
                {
                    LogRollbackFailed(_logger, rollbackException, bucket, key);
                }
            }

            await RecordFailureAsync(
                actor,
                "object.batch",
                bucket,
                exception,
                cancellationToken,
                rolledBack: uploadedKeys);
            throw;
        }
    }

    public async Task DeleteObjectAsync(
        string bucket,
        string key,
        string? versionId,
        string actor,
        CancellationToken cancellationToken)
    {
        ValidateBucketName(bucket);
        ValidateObjectKey(key);

        try
        {
            await GetStorageBucketAsync(bucket, cancellationToken);
            await _storage.DeleteObjectAsync(bucket, key, versionId, cancellationToken);

            await RecordAuditAsync(
                actor,
                "object.delete",
                $"{bucket}/{key}",
                AuditResult.Success,
                StatusNoContent,
                versionId is null ? null : new { versionId },
                cancellationToken);

            await PublishAsync(
                StorageEvent.ObjectChanged,
                new { bucket, key, change = "deleted", versionId },
                bucket,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RecordFailureAsync(actor, "object.delete", $"{bucket}/{key}", exception, cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> GetTagsAsync(
        string bucket,
        string key,
        string? versionId,
        CancellationToken cancellationToken)
    {
        await GetObjectInfoAsync(bucket, key, versionId, cancellationToken);
        return await _storage.GetTagsAsync(bucket, key, versionId, cancellationToken);
    }

    public async Task SetTagsAsync(
        string bucket,
        string key,
        string? versionId,
        IReadOnlyDictionary<string, string> tags,
        string actor,
        CancellationToken cancellationToken)
    {
        ValidateBucketName(bucket);
        ValidateObjectKey(key);
        ValidateTags(tags);

        try
        {
            await GetObjectInfoAsync(bucket, key, versionId, cancellationToken);
            await _storage.SetTagsAsync(bucket, key, tags, cancellationToken);

            await RecordAuditAsync(
                actor,
                "object.tags.update",
                $"{bucket}/{key}",
                AuditResult.Success,
                StatusOk,
                new { tags = tags.Count },
                cancellationToken);

            await PublishAsync(
                StorageEvent.ObjectChanged,
                new { bucket, key, change = "tags" },
                bucket,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RecordFailureAsync(actor, "object.tags.update", $"{bucket}/{key}", exception, cancellationToken);
            throw;
        }
    }

    public async Task<PresignedUrl> CreatePresignedUrlAsync(
        string bucket,
        string key,
        string? versionId,
        TimeSpan expiresIn,
        string actor,
        CancellationToken cancellationToken)
    {
        if (expiresIn <= TimeSpan.Zero ||
            expiresIn.TotalSeconds > _adminOptions.MaxPresignedUrlLifetimeSeconds)
        {
            throw new StorageValidationException(
                $"ExpiresInSeconds must be between 1 and {_adminOptions.MaxPresignedUrlLifetimeSeconds}.");
        }

        try
        {
            await GetObjectInfoAsync(bucket, key, versionId, cancellationToken);

            var expiresAt = _clock.UtcNow.Add(expiresIn);
            var url = _storage.CreatePresignedGetUrl(bucket, key, versionId, expiresAt);

            await RecordAuditAsync(
                actor,
                "object.presign",
                $"{bucket}/{key}",
                AuditResult.Success,
                StatusOk,
                new { expiresAt },
                cancellationToken);

            return new PresignedUrl(url, expiresAt, "GET");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RecordFailureAsync(actor, "object.presign", $"{bucket}/{key}", exception, cancellationToken);
            throw;
        }
    }

    public async Task CreateFolderAsync(
        string bucket,
        string? prefix,
        string name,
        string actor,
        CancellationToken cancellationToken)
    {
        ValidateBucketName(bucket);

        var folderName = name?.Trim() ?? string.Empty;
        if (folderName.Length == 0 ||
            folderName.Contains('/') ||
            folderName.Contains('\\') ||
            folderName is "." or "..")
        {
            throw new StorageValidationException("Folder name must be a single path segment.");
        }

        var key = $"{NormalizePrefix(prefix)}{folderName}/";
        ValidateObjectKey(key);

        try
        {
            await GetStorageBucketAsync(bucket, cancellationToken);
            using var empty = new MemoryStream(Array.Empty<byte>());
            await _storage.PutObjectAsync(
                bucket,
                key,
                "application/x-directory",
                0,
                empty,
                metadata: null,
                cancellationToken);

            await RecordAuditAsync(
                actor,
                "folder.create",
                $"{bucket}/{key}",
                AuditResult.Success,
                StatusCreated,
                null,
                cancellationToken);

            await PublishAsync(
                StorageEvent.ObjectChanged,
                new { bucket, key, change = "created" },
                bucket,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RecordFailureAsync(actor, "folder.create", $"{bucket}/{key}", exception, cancellationToken);
            throw;
        }
    }

    public Task<AuditPage> ListAuditAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        return _audit.ListAsync(query, cancellationToken);
    }

    private async Task<ObjectListing> SearchObjectsAsync(
        ObjectListQuery query,
        string? prefix,
        string delimiter,
        int maxKeys,
        CancellationToken cancellationToken)
    {
        var search = query.Search!.Trim();
        var objects = new List<ObjectInfo>();
        var prefixes = new List<string>();
        var token = query.ContinuationToken;
        var scanned = 0;
        string? nextToken = null;

        while (scanned < _adminOptions.MaxSearchObjects && objects.Count < maxKeys)
        {
            var page = await _storage.ListObjectsAsync(
                query.Bucket,
                prefix,
                delimiter,
                1000,
                token,
                cancellationToken);

            scanned += page.Objects.Count;

            objects.AddRange(page.Objects.Where(item =>
                item.ObjectKey.Contains(search, StringComparison.OrdinalIgnoreCase)));

            foreach (var folder in page.Prefixes.Where(item =>
                         item.Contains(search, StringComparison.OrdinalIgnoreCase)))
            {
                if (!prefixes.Contains(folder, StringComparer.Ordinal))
                {
                    prefixes.Add(folder);
                }
            }

            token = page.NextContinuationToken;
            nextToken = token;

            if (token is null)
            {
                break;
            }
        }

        return new ObjectListing(
            prefixes.Take(maxKeys).ToArray(),
            objects.Take(maxKeys).ToArray(),
            nextToken);
    }

    private async Task<IReadOnlyList<ObjectInfo>> EnrichContentTypesAsync(
        string bucket,
        IReadOnlyList<ObjectInfo> objects,
        CancellationToken cancellationToken)
    {
        var result = new ObjectInfo[objects.Count];
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = 8,
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(
            Enumerable.Range(0, objects.Count),
            options,
            async (index, token) =>
            {
                var item = objects[index];

                try
                {
                    var head = await _storage.HeadObjectAsync(bucket, item.ObjectKey, null, token);
                    result[index] = head is null ? item : item with { ContentType = head.ContentType };
                }
                catch (ObjectStorageException)
                {
                    result[index] = item;
                }
            });

        return result;
    }

    private async Task<BucketSummary> BuildSummaryAsync(
        StorageBucket bucket,
        BucketSettings settings,
        bool refreshUsage,
        CancellationToken cancellationToken)
    {
        var versioning = await _storage.GetVersioningAsync(bucket.Name, cancellationToken);
        var security = await _storage.GetSecurityAsync(bucket.Name, cancellationToken);
        var stats = await _storage.GetUsageAsync(bucket.Name, refreshUsage, cancellationToken);
        var usage = ToUsage(stats, settings.QuotaBytes);

        return new BucketSummary(
            bucket.Name,
            bucket.CreatedAt ?? settings.CreatedAt,
            versioning,
            security,
            settings.AccessMode,
            usage);
    }

    private async Task TryDeleteUploadedObjectAsync(
        string bucket,
        string key,
        CancellationToken cancellationToken)
    {
        try
        {
            await _storage.DeleteObjectAsync(bucket, key, versionId: null, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRollbackFailed(_logger, exception, bucket, key);
        }
    }

    private static BucketUsage ToUsage(BucketUsageStats stats, long? quotaBytes)
    {
        return new BucketUsage(
            stats.Bucket,
            stats.ObjectCount,
            stats.UsedBytes,
            quotaBytes,
            stats.CalculatedAt);
    }

    private async Task<StorageBucket> GetStorageBucketAsync(
        string bucket,
        CancellationToken cancellationToken)
    {
        ValidateBucketName(bucket);

        return await _storage.GetBucketAsync(bucket, cancellationToken)
            ?? throw new BucketNotFoundException(bucket);
    }

    private async Task<Dictionary<string, BucketSettings>> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.ListAsync(cancellationToken);
        return settings.ToDictionary(item => item.Bucket, StringComparer.Ordinal);
    }

    private async Task<BucketSettings> EnsureSettingsAsync(
        StorageBucket bucket,
        CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(bucket.Name, cancellationToken);

        if (settings is not null)
        {
            return settings;
        }

        settings = BucketSettings.Create(
            bucket.Name,
            quotaBytes: null,
            bucket.CreatedAt ?? _clock.UtcNow);

        await _settings.AddAsync(settings, cancellationToken);
        await _settings.SaveChangesAsync(cancellationToken);

        return settings;
    }

    private async Task<BucketSettings> EnsureSettingsAsync(
        StorageBucket bucket,
        Dictionary<string, BucketSettings> cache,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(bucket.Name, out var settings))
        {
            return settings;
        }

        settings = await EnsureSettingsAsync(bucket, cancellationToken);
        cache[bucket.Name] = settings;

        return settings;
    }

    private static string ValidateBucketName(string bucket)
    {
        var name = bucket?.Trim() ?? string.Empty;

        if (!BucketNamePattern.IsMatch(name) ||
            name.Contains("..", StringComparison.Ordinal) ||
            IpAddressPattern.IsMatch(name))
        {
            throw new StorageValidationException(
                "Bucket name must be 3-63 characters: lowercase letters, digits, dots and hyphens, " +
                "must start and end with a letter or digit and must not look like an IP address.");
        }

        return name;
    }

    private static void ValidateObjectKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) ||
            key.Length > 1024 ||
            key.StartsWith('/') ||
            key.Any(char.IsControl))
        {
            throw new StorageValidationException(
                "Object key must be 1-1024 characters, must not start with '/' and must not contain control characters.");
        }
    }

    private static void ValidateLifecycleRules(IReadOnlyList<LifecycleRule> rules)
    {
        if (rules is null || rules.Count > 100)
        {
            throw new StorageValidationException("Provide between 0 and 100 lifecycle rules.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id) || rule.Id.Length > 255)
            {
                throw new StorageValidationException("Every lifecycle rule requires an Id of at most 255 characters.");
            }

            if (!ids.Add(rule.Id))
            {
                throw new StorageValidationException($"Duplicate lifecycle rule Id '{rule.Id}'.");
            }

            if (rule.ExpireAfterDays is <= 0 ||
                rule.NoncurrentVersionExpireAfterDays is <= 0)
            {
                throw new StorageValidationException("Lifecycle expiration days must be positive.");
            }

            if (rule.Prefix?.StartsWith('/') == true)
            {
                throw new StorageValidationException("Lifecycle prefix must not start with '/'.");
            }
        }
    }

    private void ValidateTags(IReadOnlyDictionary<string, string> tags)
    {
        if (tags.Count > _fileOptions.MaxTags)
        {
            throw new StorageValidationException(
                $"At most {_fileOptions.MaxTags} tags are allowed.");
        }

        foreach (var (key, value) in tags)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > _fileOptions.MaxTagKeyLength)
            {
                throw new StorageValidationException(
                    $"Tag keys must be 1-{_fileOptions.MaxTagKeyLength} characters long.");
            }

            if (value.Length > _fileOptions.MaxTagValueLength)
            {
                throw new StorageValidationException(
                    $"Tag values must be at most {_fileOptions.MaxTagValueLength} characters long.");
            }
        }
    }

    private static string? NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return null;
        }

        var normalized = prefix.Trim().TrimStart('/');
        return normalized.EndsWith('/') ? normalized : normalized + "/";
    }

    private static string BuildPublicReadPolicy(string bucket)
    {
        var policy = new
        {
            Version = "2012-10-17",
            Statement = new[]
            {
                new
                {
                    Effect = "Allow",
                    Principal = new { AWS = new[] { "*" } },
                    Action = new[] { "s3:GetObject" },
                    Resource = new[] { $"arn:aws:s3:::{bucket}/*" },
                },
            },
        };

        return JsonSerializer.Serialize(policy);
    }

    private Task RecordAuditAsync(
        string actor,
        string action,
        string resource,
        AuditResult result,
        int? statusCode,
        object? details,
        CancellationToken cancellationToken)
    {
        return _auditor.RecordAsync(
            actor,
            action,
            resource,
            result,
            statusCode,
            details,
            cancellationToken);
    }

    private Task RecordFailureAsync(
        string actor,
        string action,
        string resource,
        Exception exception,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? rolledBack = null)
    {
        return RecordAuditAsync(
            actor,
            action,
            resource,
            AuditResult.Failure,
            statusCode: null,
            details: rolledBack is null
                ? new { error = exception.Message }
                : new { error = exception.Message, rolledBack },
            cancellationToken);
    }

    private ValueTask PublishAsync(
        string type,
        object data,
        string? bucket,
        CancellationToken cancellationToken)
    {
        return _events.PublishAsync(new StorageEvent(type, _clock.UtcNow, data, bucket), cancellationToken);
    }

    private static string? BucketFromResource(string resource)
    {
        var separator = resource.IndexOf('/');
        return separator < 0 ? resource : resource[..separator];
    }

    [LoggerMessage(EventId = 4002, Level = LogLevel.Warning, Message = "Failed to remove uploaded object {Bucket}/{Key} after a partial failure.")]
    private static partial void LogRollbackFailed(ILogger logger, Exception exception, string bucket, string key);
}
