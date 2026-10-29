namespace Reon.Api.Errors;

public sealed partial class CoreExceptionHandler(ILogger<CoreExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (statusCode, title, detail) = exception switch
        {
            FileMetadataNotFoundException => (
                StatusCodes.Status404NotFound,
                "File not found",
                "The requested file does not exist or was deleted."),
            MultipartUploadNotFoundException => (
                StatusCodes.Status404NotFound,
                "Multipart upload not found",
                "The requested multipart upload does not exist."),
            MultipartUploadExpiredException => (
                StatusCodes.Status410Gone,
                "Multipart upload expired",
                "The multipart upload expired and its parts were aborted."),
            MultipartUploadNotActiveException => (
                StatusCodes.Status409Conflict,
                "Multipart upload is not active",
                "The multipart upload is already completed, aborted or failed."),
            FileNotReadyException => (
                StatusCodes.Status409Conflict,
                "File is not ready",
                "The file is still being processed or is no longer available."),
            FileTooLargeException => (
                StatusCodes.Status413PayloadTooLarge,
                "File too large",
                "The uploaded file exceeds the configured limit."),
            FileValidationException validation => (
                StatusCodes.Status400BadRequest,
                "Validation failed",
                validation.Message),
            StorageValidationException adminValidation => (
                StatusCodes.Status400BadRequest,
                "Validation failed",
                adminValidation.Message),
            BucketNotFoundException bucketNotFound => (
                StatusCodes.Status404NotFound,
                "Bucket not found",
                bucketNotFound.Message),
            BucketAlreadyExistsException bucketExists => (
                StatusCodes.Status409Conflict,
                "Bucket already exists",
                bucketExists.Message),
            StoredObjectNotFoundException objectNotFound => (
                StatusCodes.Status404NotFound,
                "Object not found",
                objectNotFound.Message),
            QuotaExceededException quota => (
                StatusCodes.Status409Conflict,
                "Bucket quota exceeded",
                quota.Message),
            StorageCapabilityNotSupportedException capability => (
                StatusCodes.Status501NotImplemented,
                "Operation is not supported by the storage provider",
                capability.Message),
            ObjectStorageException storage => (
                StatusCodes.Status502BadGateway,
                "Object storage unavailable",
                "The object storage request failed. The operation can be retried."),
            _ => (0, string.Empty, string.Empty),
        };

        if (statusCode == 0)
        {
            return false;
        }

        if (exception is ObjectStorageException)
        {
            LogStorageFailure(logger, exception);
        }

        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/problem+json";

        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = httpContext.Request.Path,
        };
        problem.Extensions["traceId"] = httpContext.TraceIdentifier;

        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }

    [LoggerMessage(EventId = 3001, Level = LogLevel.Error, Message = "Object storage operation failed.")]
    private static partial void LogStorageFailure(ILogger logger, Exception exception);
}
