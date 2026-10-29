using System.Buffers;
using System.Net;
using System.Runtime.CompilerServices;
using Amazon.S3;
using Amazon.S3.Model;

namespace Reon.Infrastructure.Storage;

public sealed partial class S3ObjectStorage : IObjectStorage
{
    private readonly IAmazonS3 _s3;
    private readonly S3Options _options;
    private readonly FileStorageOptions _fileStorageOptions;
    private readonly ILogger<S3ObjectStorage> _logger;

    public S3ObjectStorage(
        IAmazonS3 s3,
        IOptions<S3Options> options,
        IOptions<FileStorageOptions> fileStorageOptions,
        ILogger<S3ObjectStorage> logger)
    {
        _s3 = s3;
        _options = options.Value;
        _fileStorageOptions = fileStorageOptions.Value;
        _logger = logger;
    }

    public string BucketName => _options.BucketName;

    public async Task<StoredObject> PutAsync(
        ObjectStoragePutRequest request,
        Stream content,
        CancellationToken cancellationToken)
    {
        try
        {
            if (request.ContentLength <= _fileStorageOptions.MultipartThresholdBytes)
            {
                return await PutSingleAsync(request, content, cancellationToken);
            }

            return await PutMultipartAsync(request, content, cancellationToken);
        }
        catch (AmazonS3Exception exception)
        {
            throw new ObjectStorageException(
                $"S3 upload failed for object '{request.ObjectKey}'.",
                exception);
        }
    }

    public async Task<ObjectInfo?> HeadAsync(string objectKey, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _s3.GetObjectMetadataAsync(
                new GetObjectMetadataRequest
                {
                    BucketName = _options.BucketName,
                    Key = objectKey,
                },
                cancellationToken);

            return new ObjectInfo(
                objectKey,
                response.ContentLength,
                response.ETag,
                response.VersionId,
                response.LastModified is null
                    ? null
                    : new DateTimeOffset(response.LastModified.Value.ToUniversalTime(), TimeSpan.Zero),
                ToDictionary(response.Metadata));
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (AmazonS3Exception exception)
        {
            throw new ObjectStorageException(
                $"S3 HEAD failed for object '{objectKey}'.",
                exception);
        }
    }

    public async Task<Stream?> OpenReadAsync(string objectKey, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _s3.GetObjectAsync(
                new GetObjectRequest
                {
                    BucketName = _options.BucketName,
                    Key = objectKey,
                },
                cancellationToken);

            return new S3ResponseStream(response);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (AmazonS3Exception exception)
        {
            throw new ObjectStorageException(
                $"S3 GET failed for object '{objectKey}'.",
                exception);
        }
    }

    public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
    {
        try
        {
            await _s3.DeleteObjectAsync(
                new DeleteObjectRequest
                {
                    BucketName = _options.BucketName,
                    Key = objectKey,
                },
                cancellationToken);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            // Idempotent delete.
        }
        catch (AmazonS3Exception exception)
        {
            throw new ObjectStorageException(
                $"S3 DELETE failed for object '{objectKey}'.",
                exception);
        }
    }

    public async IAsyncEnumerable<ObjectInfo> ListAsync(
        string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? continuationToken = null;

        while (true)
        {
            ListObjectsV2Response response;

            try
            {
                response = await _s3.ListObjectsV2Async(
                    new ListObjectsV2Request
                    {
                        BucketName = _options.BucketName,
                        Prefix = prefix,
                        ContinuationToken = continuationToken,
                    },
                    cancellationToken);
            }
            catch (AmazonS3Exception exception)
            {
                throw new ObjectStorageException(
                    $"S3 LIST failed for prefix '{prefix}'.",
                    exception);
            }

            foreach (var item in response.S3Objects ?? [])
            {
                yield return new ObjectInfo(
                    item.Key,
                    item.Size ?? 0,
                    item.ETag,
                    null,
                    item.LastModified is null
                        ? null
                        : new DateTimeOffset(item.LastModified.Value.ToUniversalTime(), TimeSpan.Zero),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            }

            if (!response.IsTruncated.GetValueOrDefault())
            {
                yield break;
            }

            continuationToken = response.NextContinuationToken;
        }
    }

    public async Task AbortIncompleteUploadsAsync(TimeSpan olderThan, CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow - olderThan;
        string? keyMarker = null;
        string? uploadIdMarker = null;

        try
        {
            do
            {
                var response = await _s3.ListMultipartUploadsAsync(
                    new ListMultipartUploadsRequest
                    {
                        BucketName = _options.BucketName,
                        KeyMarker = keyMarker,
                        UploadIdMarker = uploadIdMarker,
                    },
                    cancellationToken);

                foreach (var upload in response.MultipartUploads ?? [])
                {
                    if (upload.Initiated is not { } initiated ||
                        initiated.ToUniversalTime() >= cutoff)
                    {
                        continue;
                    }

                    await _s3.AbortMultipartUploadAsync(
                        new AbortMultipartUploadRequest
                        {
                            BucketName = _options.BucketName,
                            Key = upload.Key,
                            UploadId = upload.UploadId,
                        },
                        cancellationToken);

                    LogAbortedUpload(_logger, upload.Key, upload.UploadId);
                }

                keyMarker = response.NextKeyMarker;
                uploadIdMarker = response.NextUploadIdMarker;
            }
            while (!string.IsNullOrEmpty(keyMarker));
        }
        catch (AmazonS3Exception exception)
        {
            // Not every S3-compatible provider implements ListMultipartUploads.
            // In that case the per-upload abort in PutMultipartAsync remains the safety net.
            LogMultipartCleanupUnavailable(_logger, exception);
        }
    }

    private async Task<StoredObject> PutSingleAsync(
        ObjectStoragePutRequest request,
        Stream content,
        CancellationToken cancellationToken)
    {
        var putRequest = new PutObjectRequest
        {
            BucketName = _options.BucketName,
            Key = request.ObjectKey,
            InputStream = content,
            ContentType = request.ContentType,
            AutoCloseStream = false,
            UseChunkEncoding = false,
        };

        AddMetadata(putRequest.Metadata, request.Metadata);

        var response = await _s3.PutObjectAsync(putRequest, cancellationToken);
        return new StoredObject(request.ObjectKey, response.ETag, request.ContentLength, response.VersionId);
    }

    private async Task<StoredObject> PutMultipartAsync(
        ObjectStoragePutRequest request,
        Stream content,
        CancellationToken cancellationToken)
    {
        var partSize = _fileStorageOptions.MultipartPartSizeBytes;
        var initiateRequest = new InitiateMultipartUploadRequest
        {
            BucketName = _options.BucketName,
            Key = request.ObjectKey,
            ContentType = request.ContentType,
        };
        AddMetadata(initiateRequest.Metadata, request.Metadata);

        var initiateResponse = await _s3.InitiateMultipartUploadAsync(
            initiateRequest,
            cancellationToken);

        var uploadId = initiateResponse.UploadId;
        var partETags = new List<PartETag>();
        var buffer = ArrayPool<byte>.Shared.Rent(partSize);
        long totalSize = 0;

        try
        {
            var partNumber = 1;
            while (true)
            {
                var read = await ReadFullAsync(content, buffer.AsMemory(0, partSize), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                totalSize += read;
                using var partStream = new MemoryStream(buffer, 0, read, writable: false, publiclyVisible: true);
                var uploadPartResponse = await _s3.UploadPartAsync(
                    new UploadPartRequest
                    {
                        BucketName = _options.BucketName,
                        Key = request.ObjectKey,
                        UploadId = uploadId,
                        PartNumber = partNumber,
                        InputStream = partStream,
                        PartSize = read,
                        UseChunkEncoding = false,
                    },
                    cancellationToken);

                partETags.Add(new PartETag(partNumber, uploadPartResponse.ETag));
                partNumber++;

                if (read < partSize)
                {
                    break;
                }
            }

            var completeResponse = await _s3.CompleteMultipartUploadAsync(
                new CompleteMultipartUploadRequest
                {
                    BucketName = _options.BucketName,
                    Key = request.ObjectKey,
                    UploadId = uploadId,
                    PartETags = partETags,
                },
                cancellationToken);

            return new StoredObject(request.ObjectKey, completeResponse.ETag, totalSize, completeResponse.VersionId);
        }
        catch
        {
            try
            {
                await _s3.AbortMultipartUploadAsync(
                    new AbortMultipartUploadRequest
                    {
                        BucketName = _options.BucketName,
                        Key = request.ObjectKey,
                        UploadId = uploadId,
                    },
                    CancellationToken.None);
            }
            catch (Exception abortException)
            {
                LogAbortFailed(_logger, abortException, request.ObjectKey, uploadId);
            }

            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<int> ReadFullAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static void AddMetadata(MetadataCollection target, IReadOnlyDictionary<string, string>? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (var (key, value) in source)
        {
            target[key] = value;
        }
    }

    private static MetadataCollection ToMetadataCollection(IReadOnlyDictionary<string, string>? source)
    {
        var metadata = new MetadataCollection();
        AddMetadata(metadata, source);
        return metadata;
    }

    private static Dictionary<string, string> ToDictionary(MetadataCollection metadata)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawKey in metadata.Keys)
        {
            // S3 returns user metadata with the x-amz-meta- prefix; the rest of
            // the application works with the bare key (e.g. reon-file-id).
            var key = rawKey.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase)
                ? rawKey["x-amz-meta-".Length..]
                : rawKey;

            result[key] = metadata[rawKey];
        }

        return result;
    }

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Aborted stale multipart upload {UploadId} for object {ObjectKey}.")]
    private static partial void LogAbortedUpload(ILogger logger, string objectKey, string uploadId);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Multipart upload enumeration is not supported by the S3 provider; relying on per-upload abort.")]
    private static partial void LogMultipartCleanupUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Warning, Message = "Failed to abort multipart upload {UploadId} for object {ObjectKey}.")]
    private static partial void LogAbortFailed(ILogger logger, Exception exception, string objectKey, string uploadId);

    private sealed class S3ResponseStream(GetObjectResponse response) : Stream
    {
        private readonly Stream _inner = response.ResponseStream;

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            response.Dispose();
            await base.DisposeAsync();
        }
    }
}
