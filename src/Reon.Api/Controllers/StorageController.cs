namespace Reon.Api.Controllers;

/// <summary>
/// Информация о хранилище: статистика по файлам и параметры текущего бакета.
/// </summary>
/// <param name="files">Сервис файлового домена: статистика по метаданным.</param>
/// <param name="objectStorage">Объектное хранилище: имя бакета для ответа.</param>
/// <param name="options">Лимиты файлового API: префикс ключей и максимальный размер.</param>
[ApiController]
[Route("api/storage")]
[Produces("application/json")]
public sealed class StorageController(
    FileStorageService files,
    IObjectStorage objectStorage,
    IOptions<FileStorageOptions> options) : ControllerBase
{
    [HttpGet("stats")]
    [ProducesResponseType<StorageStatsResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<StorageStatsResponse>> GetStats(CancellationToken cancellationToken)
    {
        var stats = await files.GetStatsAsync(cancellationToken);
        return Ok(new StorageStatsResponse(
            stats.TotalFiles,
            stats.TotalBytes,
            stats.ByStatus,
            stats.BytesByContentType));
    }

    [HttpGet("info")]
    [ProducesResponseType<StorageInfoResponse>(StatusCodes.Status200OK)]
    public ActionResult<StorageInfoResponse> GetInfo()
    {
        return Ok(new StorageInfoResponse(
            objectStorage.BucketName,
            options.Value.KeyPrefix,
            options.Value.MaxFileSizeBytes,
            options.Value.MultipartPartSizeBytes,
            options.Value.MinMultipartPartSizeBytes,
            options.Value.MaxMultipartParts,
            options.Value.MultipartUploadLifetimeDays));
    }
}
