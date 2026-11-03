namespace Reon.Api.Controllers;

/// <summary>
/// Скачивание по временной подписанной ссылке. Ссылка ведёт на API,
/// а не на S3: клиент не получает сетевой доступ к объектному хранилищу.
/// </summary>
/// <param name="admin">Открывает объект в S3 от имени сервиса.</param>
/// <param name="adminOptions">Публичный адрес API и секрет подписи ссылок.</param>
[ApiController]
[Route("api/admin/download")]
[Produces("application/octet-stream")]
public sealed class AdminDownloadsController(
    IStorageAdminService admin,
    IOptions<AdminOptions> adminOptions) : ControllerBase
{
    /// <summary>Скачать объект по подписанной ссылке</summary>
    /// <remarks>
    /// Проверяет HMAC-подпись и срок действия. Ссылка создаётся
    /// эндпоинтом <c>QUERY .../presigned-urls</c> и указывает на этот метод.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway, "application/problem+json")]
    public async Task<IActionResult> Download(
        [FromQuery] string? bucket,
        [FromQuery] string? key,
        [FromQuery] string? versionId,
        [FromQuery] long expires,
        [FromQuery] string? signature,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(bucket) ||
            string.IsNullOrWhiteSpace(key) ||
            string.IsNullOrWhiteSpace(signature))
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid download link",
                detail: "The download link is missing required parameters.");
        }

        var options = adminOptions.Value;
        var valid = DownloadLinkSigner.Validate(
            options.DownloadLinkSecret,
            bucket,
            key,
            versionId,
            expires,
            signature,
            DateTimeOffset.UtcNow);

        if (!valid)
        {
            return Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Download link is invalid or expired",
                detail: "The signed download link is invalid or has expired.");
        }

        var content = await admin.OpenObjectAsync(bucket, key, versionId, cancellationToken);

        if (!string.IsNullOrWhiteSpace(content.ETag))
        {
            Response.Headers.ETag = content.ETag;
        }

        if (!string.IsNullOrWhiteSpace(content.VersionId))
        {
            Response.Headers["X-Reon-Version-Id"] = content.VersionId;
        }

        var fileName = Path.GetFileName(key);
        return File(
            content.Stream,
            content.ContentType,
            string.IsNullOrWhiteSpace(fileName) ? "download" : fileName,
            enableRangeProcessing: false);
    }
}
