using System.Diagnostics;
using OpenTelemetry;

namespace Reon.Api.Telemetry;

/// <summary>
/// Приводит спаны AWS SDK к профилю REON: бакет дублируется в <c>s3.bucket</c>,
/// а ключ объекта в спаны не попадает.
/// </summary>
internal sealed class S3SpanProcessor : BaseProcessor<Activity>
{
    private const string RpcSystem = "rpc.system";
    private const string RpcService = "rpc.service";
    private const string AwsS3Bucket = "aws.s3.bucket";
    private const string AwsS3Key = "aws.s3.key";
    private const string S3Bucket = "s3.bucket";

    /// <inheritdoc/>
    public override void OnEnd(Activity activity)
    {
        if (activity.GetTagItem(RpcSystem) as string != "aws-api" ||
            activity.GetTagItem(RpcService) as string != "S3")
        {
            return;
        }

        if (activity.GetTagItem(AwsS3Bucket) is string bucket)
        {
            activity.SetTag(S3Bucket, bucket);
        }

        // Установка null удаляет тег: ключ объекта и его дубль в aws.s3.bucket
        // не должны уходить в телеметрию.
        activity.SetTag(AwsS3Key, null);
        activity.SetTag(AwsS3Bucket, null);
    }
}
