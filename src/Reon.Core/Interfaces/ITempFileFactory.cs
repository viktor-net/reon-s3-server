namespace Reon.Core.Interfaces;

public interface ITempFileFactory
{
    /// <summary>
    /// Копирует исходный поток во временный файл, проверяет лимит размера
    /// и считает SHA-256. Полученный файл должен освободить вызывающий код.
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
            // Best-effort очистка. Оставшийся временный файл не влияет на корректность.
        }

        return ValueTask.CompletedTask;
    }
}
