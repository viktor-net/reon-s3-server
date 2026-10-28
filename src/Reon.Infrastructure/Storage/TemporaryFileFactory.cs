using System.Buffers;
using System.Security.Cryptography;
using Reon.Core.Exceptions;
using Reon.Core.Interfaces;

namespace Reon.Infrastructure.Storage;

public sealed class TemporaryFileFactory : ITempFileFactory
{
    public async Task<TempFile> CreateAsync(Stream source, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        var path = Path.Combine(Path.GetTempPath(), $"reon-s3-{Guid.NewGuid():N}.tmp");

        try
        {
            await using var target = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            long total = 0;

            try
            {
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        throw new FileTooLargeException(maxBytes);
                    }

                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            await target.FlushAsync(cancellationToken);

            var sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            return new TempFile(path, total, sha256);
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp file.
        }
    }
}
