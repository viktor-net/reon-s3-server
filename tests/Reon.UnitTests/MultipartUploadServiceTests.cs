namespace Reon.UnitTests;

public sealed class MultipartUploadServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeObjectStorage _storage = new();
    private readonly FakeFileMetadataRepository _files = new();
    private readonly FakeMultipartUploadRepository _uploads = new();
    private readonly FakeClock _clock = new(Now);
    private readonly MultipartUploadService _service;

    public MultipartUploadServiceTests()
    {
        var options = Options.Create(
            new FileStorageOptions
            {
                DefaultOwner = "system",
                MaxFileSizeBytes = 1024 * 1024,
                MultipartPartSizeBytes = 5 * 1024 * 1024,
                MinMultipartPartSizeBytes = 4,
                MaxMultipartParts = 10,
                MultipartUploadLifetimeDays = 7,
                KeyPrefix = "files/",
            });

        _service = new MultipartUploadService(
            _storage,
            _uploads,
            _files,
            new TemporaryFileFactory(),
            _clock,
            options,
            NullLogger<MultipartUploadService>.Instance);
    }

    [Fact]
    public async Task InitiateUploadPartAndComplete_CreatesReadyFile()
    {
        var upload = await _service.InitiateAsync(
            "big.txt",
            "text/plain",
            "multipart",
            new Dictionary<string, string> { ["kind"] = "test" },
            CancellationToken.None);

        Assert.Equal(MultipartUploadStatus.Initiated, upload.Status);

        var part = await _service.UploadPartAsync(
            upload.Id,
            1,
            new MemoryStream("hello"u8.ToArray()),
            contentLength: 5,
            cancellationToken: CancellationToken.None);

        Assert.Equal(1, part.PartNumber);
        Assert.Equal(5, part.Size);

        var file = await _service.CompleteAsync(upload.Id, CancellationToken.None);

        Assert.Equal(FileStatus.Ready, file.Status);
        Assert.Equal(5, file.Size);
        Assert.EndsWith(".txt", file.ObjectKey, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(file.Sha256));
        Assert.Single(_files.Files);
        Assert.Empty(_storage.MultipartUploads);
    }

    [Fact]
    public async Task AbortAsync_MarksUploadAbortedAndRemovesS3Upload()
    {
        var upload = await _service.InitiateAsync(
            "abort.bin",
            "application/octet-stream",
            null,
            null,
            CancellationToken.None);

        await _service.AbortAsync(upload.Id, CancellationToken.None);

        Assert.Equal(MultipartUploadStatus.Aborted, upload.Status);
        Assert.Empty(_storage.MultipartUploads);
    }

    [Fact]
    public async Task UploadPartAsync_WhenUploadIsExpired_AbortsAndThrows()
    {
        var upload = await _service.InitiateAsync(
            "expired.bin",
            "application/octet-stream",
            null,
            null,
            CancellationToken.None);

        _clock.UtcNow = Now.AddDays(8);

        await Assert.ThrowsAsync<MultipartUploadExpiredException>(() =>
            _service.UploadPartAsync(
                upload.Id,
                1,
                new MemoryStream("data"u8.ToArray()),
                contentLength: 4,
                cancellationToken: CancellationToken.None));

        Assert.Equal(MultipartUploadStatus.Expired, upload.Status);
        Assert.Empty(_storage.MultipartUploads);
    }
}
