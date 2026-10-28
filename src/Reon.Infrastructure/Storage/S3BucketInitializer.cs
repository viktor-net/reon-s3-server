using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reon.Infrastructure.Configuration;

namespace Reon.Infrastructure.Storage;

public sealed partial class S3BucketInitializer : IHostedService
{
    private readonly IAmazonS3 _s3;
    private readonly S3Options _options;
    private readonly ILogger<S3BucketInitializer> _logger;

    public S3BucketInitializer(
        IAmazonS3 s3,
        IOptions<S3Options> options,
        ILogger<S3BucketInitializer> logger)
    {
        _s3 = s3;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.EnsureBucket)
        {
            return;
        }

        try
        {
            if (await AmazonS3Util.DoesS3BucketExistV2Async(_s3, _options.BucketName))
            {
                LogBucketExists(_logger, _options.BucketName);
                return;
            }

            await _s3.PutBucketAsync(
                new PutBucketRequest
                {
                    BucketName = _options.BucketName,
                },
                cancellationToken);

            LogBucketCreated(_logger, _options.BucketName);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.Conflict)
        {
            // Another instance created the bucket in parallel.
            LogBucketExists(_logger, _options.BucketName);
        }
        catch (AmazonS3Exception exception)
        {
            throw new InvalidOperationException(
                $"Could not ensure S3 bucket '{_options.BucketName}' at '{_options.ServiceUrl}'.",
                exception);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 2101, Level = LogLevel.Information, Message = "S3 bucket {BucketName} is available.")]
    private static partial void LogBucketExists(ILogger logger, string bucketName);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Information, Message = "S3 bucket {BucketName} was created.")]
    private static partial void LogBucketCreated(ILogger logger, string bucketName);
}
