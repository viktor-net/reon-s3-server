namespace Reon.UnitTests;

public sealed class FileMetadataRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MarkExpired_WhenDue_TransitionsToExpired()
    {
        var file = CreateReady(Now.AddMinutes(1));

        file.MarkExpired(Now.AddMinutes(2));

        Assert.Equal(FileStatus.Expired, file.Status);
        Assert.Equal(Now.AddMinutes(2), file.ExpiredAt);
        Assert.True(file.IsExpired(Now.AddMinutes(2)));
    }

    [Fact]
    public void MarkExpired_WhenHold_DoesNothing()
    {
        var file = CreateReady(Now.AddMinutes(1));
        file.SetRetentionHold(true, Now);

        file.MarkExpired(Now.AddMinutes(2));

        Assert.Equal(FileStatus.Ready, file.Status);
        Assert.False(file.IsExpired(Now.AddMinutes(2)));
    }

    [Fact]
    public void SetRetentionHold_OnExpiredFile_ReturnsToReady()
    {
        var file = CreateReady(Now.AddMinutes(1));
        file.MarkExpired(Now.AddMinutes(2));

        file.SetRetentionHold(true, Now.AddMinutes(3));

        Assert.Equal(FileStatus.Ready, file.Status);
        Assert.Null(file.ExpiredAt);
        Assert.False(file.IsExpired(Now.AddMinutes(4)));
    }

    [Fact]
    public void ExtendRetention_OnExpiredFile_ReturnsToReady()
    {
        var file = CreateReady(Now.AddMinutes(1));
        file.MarkExpired(Now.AddMinutes(2));

        file.ExtendRetention(Now.AddHours(1), Now.AddMinutes(3));

        Assert.Equal(FileStatus.Ready, file.Status);
        Assert.Null(file.ExpiredAt);
        Assert.Equal(Now.AddHours(1), file.ExpiresAt);
    }

    private static FileMetadata CreateReady(DateTimeOffset? expiresAt)
    {
        var file = FileMetadata.CreatePending(
            Guid.CreateVersion7(),
            "test-bucket",
            "files/test.txt",
            "test.txt",
            "text/plain",
            4,
            new string('a', 64),
            "system",
            null,
            null,
            1,
            Now,
            expiresAt);

        file.MarkReady("\"etag\"", null, Now);
        return file;
    }
}
