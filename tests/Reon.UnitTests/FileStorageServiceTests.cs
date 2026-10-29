namespace Reon.UnitTests;

public sealed class FileStorageServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeObjectStorage _storage = new();
    private readonly FakeFileMetadataRepository _repository = new();
    private readonly FakeClock _clock = new(Now);
    private readonly FileStorageService _service;

    public FileStorageServiceTests()
    {
        var options = Options.Create(
            new FileStorageOptions
            {
                MaxFileSizeBytes = 1024 * 1024,
                MultipartThresholdBytes = 5 * 1024 * 1024,
                MultipartPartSizeBytes = 5 * 1024 * 1024,
                KeyPrefix = "files/",
                DefaultOwner = "system",
            });

        _service = new FileStorageService(
            _storage,
            _repository,
            new TemporaryFileFactory(),
            _clock,
            options,
            new FakeStorageQuotaGuard(),
            NullLogger<FileStorageService>.Instance);
    }

    [Fact]
    public async Task UploadAsync_StoresObjectAndReturnsReadyMetadata()
    {
        var content = "hello from reon"u8.ToArray();

        var file = await _service.UploadAsync(
            new MemoryStream(content),
            new UploadFileCommand("hello.txt", "text/plain", "greeting", new Dictionary<string, string> { ["kind"] = "test" }),
            CancellationToken.None);

        Assert.Equal(FileStatus.Ready, file.Status);
        Assert.Equal("hello.txt", file.OriginalName);
        Assert.Equal("text/plain", file.ContentType);
        Assert.Equal(content.Length, file.Size);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant(), file.Sha256);
        Assert.Equal("system", file.Owner);
        Assert.EndsWith(".txt", file.ObjectKey, StringComparison.OrdinalIgnoreCase);
        Assert.True(_storage.Objects.ContainsKey(file.ObjectKey));
        Assert.Single(_repository.Files);
    }

    [Fact]
    public async Task UploadAsync_WhenStorageFails_MarksFileFailedAndRethrows()
    {
        _storage.FailOnPut = true;

        await Assert.ThrowsAsync<ObjectStorageException>(() =>
            _service.UploadAsync(
                new MemoryStream("data"u8.ToArray()),
                new UploadFileCommand("data.bin", "application/octet-stream", null, null),
                CancellationToken.None));

        var file = Assert.Single(_repository.Files);
        Assert.Equal(FileStatus.Failed, file.Status);
        Assert.NotNull(file.FailureReason);
    }

    [Fact]
    public async Task OpenContentAsync_WhenObjectIsMissing_MarksMissingAndThrowsNotFound()
    {
        var file = await UploadAsync();
        _storage.Objects.Remove(file.ObjectKey);

        await Assert.ThrowsAsync<FileMetadataNotFoundException>(() =>
            _service.OpenContentAsync(
                file.Id,
                CancellationToken.None));

        Assert.Equal(FileStatus.Missing, file.Status);
    }

    [Fact]
    public async Task DeleteAsync_DeletesObjectAndMarksMetadataDeleted()
    {
        var file = await UploadAsync();

        var outcome = await _service.DeleteAsync(
            file.Id,
            CancellationToken.None);

        Assert.Equal(DeleteOutcome.Deleted, outcome);
        Assert.Equal(FileStatus.Deleted, file.Status);
        Assert.False(_storage.Objects.ContainsKey(file.ObjectKey));

        var secondOutcome = await _service.DeleteAsync(
            file.Id,
            CancellationToken.None);

        Assert.Equal(DeleteOutcome.AlreadyDeleted, secondOutcome);
    }

    [Fact]
    public async Task UploadAsync_WhenFileIsTooLarge_ThrowsAndDoesNotCreateMetadata()
    {
        var options = Options.Create(
            new FileStorageOptions
            {
                MaxFileSizeBytes = 4,
                MultipartThresholdBytes = 5 * 1024 * 1024,
                MultipartPartSizeBytes = 5 * 1024 * 1024,
            });

        var service = new FileStorageService(
            _storage,
            _repository,
            new TemporaryFileFactory(),
            _clock,
            options,
            new FakeStorageQuotaGuard(),
            NullLogger<FileStorageService>.Instance);

        await Assert.ThrowsAsync<FileTooLargeException>(() =>
            service.UploadAsync(
                new MemoryStream("12345"u8.ToArray()),
                new UploadFileCommand("big.bin", null, null, null),
                CancellationToken.None));

        Assert.Empty(_repository.Files);
    }

    [Fact]
    public async Task UploadAsync_WhenQuotaExceeded_ThrowsAndCreatesNoMetadata()
    {
        var options = Options.Create(
            new FileStorageOptions
            {
                MaxFileSizeBytes = 1024 * 1024,
                MultipartThresholdBytes = 5 * 1024 * 1024,
                MultipartPartSizeBytes = 5 * 1024 * 1024,
            });

        var service = new FileStorageService(
            _storage,
            _repository,
            new TemporaryFileFactory(),
            _clock,
            options,
            new FakeStorageQuotaGuard { QuotaBytes = 4 },
            NullLogger<FileStorageService>.Instance);

        await Assert.ThrowsAsync<QuotaExceededException>(() =>
            service.UploadAsync(
                new MemoryStream("12345"u8.ToArray()),
                new UploadFileCommand("big.bin", null, null, null),
                CancellationToken.None));

        Assert.Empty(_repository.Files);
    }

    private Task<Reon.Core.Entities.FileMetadata> UploadAsync()
    {
        return _service.UploadAsync(
            new MemoryStream("hello"u8.ToArray()),
            new UploadFileCommand("hello.txt", "text/plain", null, null),
            CancellationToken.None);
    }
}
