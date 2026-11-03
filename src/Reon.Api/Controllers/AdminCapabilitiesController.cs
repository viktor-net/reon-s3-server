namespace Reon.Api.Controllers;

/// <summary>
/// Возможности текущего S3-провайдера. Интерфейс использует их, чтобы не
/// показывать действия, которые гарантированно вернут 501.
/// </summary>
/// <param name="capabilities">Реестр возможностей в Postgres.</param>
/// <param name="fileOptions">Лимиты загрузки, общие с файловым API.</param>
/// <param name="clock">Время формирования ответа.</param>
[ApiController]
[Route("api/admin/capabilities")]
[Produces("application/json")]
public sealed class AdminCapabilitiesController(
    IStorageCapabilityStore capabilities,
    IOptions<FileStorageOptions> fileOptions,
    IClock clock) : ControllerBase
{
    /// <summary>Возможности провайдера</summary>
    /// <remarks>Флаги versioning, object tags, lifecycle, bucket policy и
    /// Object Lock, а также максимальный размер загружаемого файла.</remarks>
    [HttpGet]
    [ProducesResponseType<StorageCapabilitiesResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<StorageCapabilitiesResponse>> Get(
        CancellationToken cancellationToken)
    {
        var values = await capabilities.GetAsync(cancellationToken);

        return Ok(new StorageCapabilitiesResponse(
            "s3",
            values.Versioning,
            values.ObjectTags,
            values.Lifecycle,
            values.BucketPolicy,
            values.ObjectLock,
            fileOptions.Value.MaxFileSizeBytes,
            clock.UtcNow));
    }
}
