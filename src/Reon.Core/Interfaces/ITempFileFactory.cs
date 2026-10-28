namespace Reon.Core.Interfaces;

public interface ITempFileFactory
{
    /// <summary>
    /// Copies the source stream to a temporary file, enforces the size limit
    /// and computes SHA-256. The returned file must be disposed by the caller.
    /// </summary>
    Task<TempFile> CreateAsync(Stream source, long maxBytes, CancellationToken cancellationToken);
}

public sealed class TempFile : IAsyncDisposable
{
    private bool _disposed;

    public TempFile(string path, long size, string sha256)
    {
        Path = path;
        Size = size;
        Sha256 = sha256;
    }

    public string Path { get; }

    public long Size { get; }

    public string Sha256 { get; }

    public FileStream OpenRead()
    {
        return new FileStream(
            Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        try
        {
            File.Delete(Path);
        }
        catch (IOException)
        {
            // Best-effort cleanup. A leftover temp file is not a correctness issue.
        }

        return ValueTask.CompletedTask;
    }
}
