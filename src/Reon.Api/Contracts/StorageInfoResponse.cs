namespace Reon.Api.Contracts;

public sealed record StorageInfoResponse(
    string Bucket,
    string KeyPrefix,
    long MaxFileSizeBytes,
    int MultipartPartSizeBytes,
    int MinMultipartPartSizeBytes,
    int MaxMultipartParts,
    int MultipartUploadLifetimeDays);
