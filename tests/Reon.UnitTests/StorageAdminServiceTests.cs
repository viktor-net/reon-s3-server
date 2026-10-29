namespace Reon.UnitTests;

public sealed class StorageAdminServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeObjectStorageAdmin _storage = new();
    private readonly FakeBucketSettingsRepository _settings = new();
    private readonly FakeAuditRepository _audit = new();
    private readonly FakeStorageEventPublisher _events = new();
    private readonly FakeClock _clock = new(Now);
    private readonly StorageAdminService _service;

    public StorageAdminServiceTests()
    {
        var fileOptions = Options.Create(
            new FileStorageOptions
            {
                MaxFileSizeBytes = 1024 * 1024,
                MaxTags = 50,
                MaxTagKeyLength = 64,
                MaxTagValueLength = 256,
            });

        var adminOptions = Options.Create(new AdminOptions());

        _service = new StorageAdminService(
            _storage,
            _settings,
            _audit,
            new FakeStorageAuditor(_audit),
            new FakeStorageQuotaGuard(_settings, _storage),
            _events,
            _clock,
            fileOptions,
            adminOptions,
            NullLogger<StorageAdminService>.Instance);
    }

    [Theory]
    [InlineData("Documents")]
    [InlineData("ab")]
    [InlineData("bad_name")]
    [InlineData("192.168.0.1")]
    [InlineData("double..dot")]
    [InlineData("-leading")]
    public async Task CreateBucketAsync_WithInvalidName_Throws(string name)
    {
        await Assert.ThrowsAsync<StorageValidationException>(() =>
            _service.CreateBucketAsync(
                new CreateBucketCommand(name, false, false, null),
                "admin",
                CancellationToken.None));
    }

    [Fact]
    public async Task CreateBucketAsync_WhenBucketExists_ThrowsAndRecordsFailure()
    {
        _storage.Buckets["documents"] = Now;

        await Assert.ThrowsAsync<BucketAlreadyExistsException>(() =>
            _service.CreateBucketAsync(
                new CreateBucketCommand("documents", false, false, null),
                "admin",
                CancellationToken.None));

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(AuditResult.Failure, entry.Result);
        Assert.Equal("bucket.create", entry.Action);
        Assert.Equal("documents", entry.Resource);
    }

    [Fact]
    public async Task CreateBucketAsync_PersistsSettingsAndPublishesEvent()
    {
        var summary = await _service.CreateBucketAsync(
            new CreateBucketCommand("documents", VersioningEnabled: true, ObjectLockEnabled: false, QuotaBytes: 1000),
            "admin",
            CancellationToken.None);

        Assert.Equal("documents", summary.Name);
        Assert.True(summary.Versioning.Enabled);
        Assert.Equal(1000, summary.Usage.QuotaBytes);

        var settings = await _settings.GetAsync("documents", CancellationToken.None);
        Assert.NotNull(settings);
        Assert.Equal(1000, settings.QuotaBytes);
        Assert.Equal(BucketAccessMode.Private, settings.AccessMode);

        Assert.Contains(_audit.Entries, entry =>
            entry.Action == "bucket.create" && entry.Result == AuditResult.Success);

        Assert.Contains(_events.Events, storageEvent =>
            storageEvent.Type == StorageEvent.BucketChanged && storageEvent.Bucket == "documents");
    }

    [Fact]
    public async Task GetBucketAsync_WhenBucketIsMissing_Throws()
    {
        await Assert.ThrowsAsync<BucketNotFoundException>(() =>
            _service.GetBucketAsync("missing", refreshUsage: false, CancellationToken.None));
    }

    [Fact]
    public async Task UploadObjectAsync_WhenQuotaExceeded_Throws()
    {
        _storage.Buckets["documents"] = Now;
        await _settings.AddAsync(BucketSettings.Create("documents", quotaBytes: 4, Now), CancellationToken.None);

        await Assert.ThrowsAsync<QuotaExceededException>(() =>
            _service.UploadObjectAsync(
                new UploadObjectCommand("documents", "hello.txt", "text/plain", 5),
                new MemoryStream("hello"u8.ToArray()),
                "operator",
                CancellationToken.None));
    }

    [Fact]
    public async Task UploadObjectAsync_StoresObjectTagsAndAudit()
    {
        _storage.Buckets["documents"] = Now;

        var info = await _service.UploadObjectAsync(
            new UploadObjectCommand(
                "documents",
                "contracts/contract.pdf",
                "application/pdf",
                4,
                new Dictionary<string, string> { ["author"] = "legal" },
                new Dictionary<string, string> { ["stage"] = "signed" }),
            new MemoryStream("data"u8.ToArray()),
            "operator",
            CancellationToken.None);

        Assert.Equal("contracts/contract.pdf", info.ObjectKey);
        Assert.True(_storage.Objects["documents"].ContainsKey("contracts/contract.pdf"));
        Assert.Equal("signed", _storage.Tags["documents"]["stage"]);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal("object.upload", entry.Action);
        Assert.Equal("documents/contracts/contract.pdf", entry.Resource);
        Assert.Equal("operator", entry.Actor);

        Assert.Contains(_events.Events, storageEvent =>
            storageEvent.Type == StorageEvent.ObjectChanged &&
            storageEvent.Bucket == "documents");
    }

    [Fact]
    public async Task UploadObjectAsync_WhenTaggingIsUnsupported_RemovesUploadedObject()
    {
        _storage.Buckets["documents"] = Now;
        _storage.NotSupportedTagging = true;

        await Assert.ThrowsAsync<StorageCapabilityNotSupportedException>(() =>
            _service.UploadObjectAsync(
                new UploadObjectCommand(
                    "documents",
                    "tagged.txt",
                    "text/plain",
                    4,
                    Tags: new Dictionary<string, string> { ["stage"] = "draft" }),
                new MemoryStream("data"u8.ToArray()),
                "operator",
                CancellationToken.None));

        Assert.False(_storage.Objects["documents"].ContainsKey("tagged.txt"));
    }

    [Fact]
    public async Task SetBucketAccessAsync_PublicRead_WritesGetObjectPolicy()
    {
        _storage.Buckets["documents"] = Now;

        var access = await _service.SetBucketAccessAsync(
            "documents",
            BucketAccessMode.PublicRead,
            "admin",
            CancellationToken.None);

        Assert.Equal(BucketAccessMode.PublicRead, access.Mode);
        Assert.Contains("s3:GetObject", access.Policy);
        Assert.Contains("arn:aws:s3:::documents/*", access.Policy);
        Assert.Equal(access.Policy, _storage.Policies["documents"]);

        var settings = await _settings.GetAsync("documents", CancellationToken.None);
        Assert.Equal(BucketAccessMode.PublicRead, settings!.AccessMode);
    }

    [Fact]
    public async Task SetBucketAccessAsync_Private_RemovesPolicy()
    {
        _storage.Buckets["documents"] = Now;
        _storage.Policies["documents"] = "{\"Statement\":[]}";

        var access = await _service.SetBucketAccessAsync(
            "documents",
            BucketAccessMode.Private,
            "admin",
            CancellationToken.None);

        Assert.Equal(BucketAccessMode.Private, access.Mode);
        Assert.Null(_storage.Policies["documents"]);
    }

    [Fact]
    public async Task CreateFolderAsync_CreatesZeroBytePrefixMarker()
    {
        _storage.Buckets["documents"] = Now;

        await _service.CreateFolderAsync(
            "documents",
            prefix: "contracts",
            name: "2026",
            "admin",
            CancellationToken.None);

        Assert.True(_storage.Objects["documents"].ContainsKey("contracts/2026/"));
        Assert.Empty(_storage.Objects["documents"]["contracts/2026/"]);
    }

    [Fact]
    public async Task CreateFolderAsync_WithSlashInName_Throws()
    {
        _storage.Buckets["documents"] = Now;

        await Assert.ThrowsAsync<StorageValidationException>(() =>
            _service.CreateFolderAsync(
                "documents",
                prefix: null,
                name: "a/b",
                "admin",
                CancellationToken.None));
    }

    [Fact]
    public async Task RefreshUsageAsync_ReturnsUsageAndPublishesEvent()
    {
        _storage.Buckets["documents"] = Now;
        _storage.Objects["documents"] = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["a.txt"] = new byte[10],
            ["b.txt"] = new byte[20],
        };

        var usage = await _service.RefreshUsageAsync("documents", "admin", CancellationToken.None);

        Assert.Equal(2, usage.ObjectCount);
        Assert.Equal(30, usage.UsedBytes);
        Assert.Equal("NoQuota", usage.Status);

        Assert.Contains(_events.Events, storageEvent => storageEvent.Type == StorageEvent.UsageUpdated);
    }

    [Fact]
    public async Task UploadBatchAsync_WhenLaterEntryFails_RemovesAlreadyWrittenObjects()
    {
        _storage.Buckets["documents"] = Now;

        var entries = new[]
        {
            new BatchUploadEntry(
                "first.txt",
                "text/plain",
                5,
                _ => ValueTask.FromResult<Stream>(new MemoryStream("first"u8.ToArray()))),
            new BatchUploadEntry(
                "",
                "text/plain",
                6,
                _ => ValueTask.FromResult<Stream>(new MemoryStream("second"u8.ToArray()))),
        };

        await Assert.ThrowsAsync<StorageValidationException>(() =>
            _service.UploadBatchAsync("documents", entries, null, null, "operator", CancellationToken.None));

        Assert.Empty(_storage.Objects["documents"]);
        Assert.Contains(_audit.Entries, entry =>
            entry.Action == "object.batch" && entry.Result == AuditResult.Failure);
    }

    [Fact]
    public async Task UploadBatchAsync_WhenAllEntriesSucceed_ReturnsAllObjects()
    {
        _storage.Buckets["documents"] = Now;

        var entries = new[]
        {
            new BatchUploadEntry(
                "a.txt",
                "text/plain",
                1,
                _ => ValueTask.FromResult<Stream>(new MemoryStream("a"u8.ToArray()))),
            new BatchUploadEntry(
                "b.txt",
                "text/plain",
                1,
                _ => ValueTask.FromResult<Stream>(new MemoryStream("b"u8.ToArray()))),
        };

        var uploaded = await _service.UploadBatchAsync(
            "documents",
            entries,
            null,
            null,
            "operator",
            CancellationToken.None);

        Assert.Equal(2, uploaded.Count);
        Assert.Equal(2, _storage.Objects["documents"].Count);
    }

    [Theory]
    [InlineData(50L, 100L, "Ok")]
    [InlineData(85L, 100L, "Warning")]
    [InlineData(96L, 100L, "Critical")]
    [InlineData(50L, null, "NoQuota")]
    public void BucketUsage_ComputesStatus(long usedBytes, long? quotaBytes, string expectedStatus)
    {
        var usage = new BucketUsage("documents", 1, usedBytes, quotaBytes, Now);

        Assert.Equal(expectedStatus, usage.Status);
    }
}
