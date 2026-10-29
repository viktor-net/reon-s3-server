namespace Reon.Api.Controllers;

/// <summary>
/// Сессии параллельной загрузки файла по частям (multipart-загрузки):
/// начало, загрузка частей, завершение и отмена.
/// </summary>
/// <param name="uploads">Сценарии параллельной загрузки файла по частям
/// (multipart-загрузки) и связь с файловым доменом.</param>
/// <param name="auditor">Журнал операций: актор и результат шагов загрузки.</param>
/// <param name="options">Лимиты частей и срок жизни сессии.</param>
/// <param name="adminOptions">Настройки фасада: актор по умолчанию.</param>
[ApiController]
[Route("api/uploads")]
[Produces("application/json")]
public sealed class UploadsController(
    IMultipartUploadService uploads,
    IStorageAuditor auditor,
    IOptions<FileStorageOptions> options,
    IOptions<AdminOptions> adminOptions) : ReonApiControllerBase(auditor, adminOptions)
{
    [HttpPost]
    [ProducesResponseType<MultipartUploadResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway, "application/problem+json")]
    public async Task<ActionResult<MultipartUploadResponse>> Initiate(
        [FromBody] InitiateMultipartUploadRequest request,
        CancellationToken cancellationToken)
    {
        Reon.Core.Entities.MultipartUpload upload;
        try
        {
            upload = await uploads.InitiateAsync(
                request.OriginalName,
                request.ContentType,
                request.Description,
                request.Tags,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await AuditFailureAsync("upload.begin", "uploads", exception, cancellationToken);
            throw;
        }

        await AuditAsync(
            "upload.begin",
            $"upload/{upload.Id}",
            AuditResult.Success,
            StatusCodes.Status201Created,
            new { upload.OriginalName, upload.ContentType },
            cancellationToken);

        var response = ToResponse(upload, []);
        return CreatedAtAction(nameof(Get), new { uploadId = upload.Id }, response);
    }

    [HttpGet("{uploadId:guid}")]
    [ProducesResponseType<MultipartUploadResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<MultipartUploadResponse>> Get(
        Guid uploadId,
        CancellationToken cancellationToken)
    {
        var (upload, parts) = await uploads.GetAsync(uploadId, cancellationToken);
        return Ok(ToResponse(upload, parts));
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<MultipartUploadResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<MultipartUploadResponse>>> List(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var active = await uploads.ListActiveAsync(limit, cancellationToken);
        IReadOnlyList<MultipartUploadResponse> response = active
            .Select(upload => ToResponse(upload, []))
            .ToArray();

        return Ok(response);
    }

    [HttpPut("{uploadId:guid}/parts/{partNumber:int}")]
    [Consumes("application/octet-stream")]
    [ProducesResponseType<MultipartPartResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status410Gone, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status411LengthRequired, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway, "application/problem+json")]
    public async Task<ActionResult<MultipartPartResponse>> UploadPart(
        Guid uploadId,
        int partNumber,
        CancellationToken cancellationToken)
    {
        if (Request.ContentLength is null or <= 0)
        {
            return Problem(
                statusCode: StatusCodes.Status411LengthRequired,
                title: "Content-Length required",
                detail: "The request must include a positive Content-Length for a multipart part.");
        }

        var part = await uploads.UploadPartAsync(
            uploadId,
            partNumber,
            Request.Body,
            Request.ContentLength.Value,
            cancellationToken);

        return Ok(new MultipartPartResponse(
            part.PartNumber,
            part.ETag,
            part.Size,
            part.UploadedAt));
    }

    [HttpPost("{uploadId:guid}/complete")]
    [ProducesResponseType<FileMetadataResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status410Gone, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway, "application/problem+json")]
    public async Task<ActionResult<FileMetadataResponse>> Complete(
        Guid uploadId,
        CancellationToken cancellationToken)
    {
        FileMetadata file;
        try
        {
            file = await uploads.CompleteAsync(uploadId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await AuditFailureAsync("upload.complete", $"upload/{uploadId}", exception, cancellationToken);
            throw;
        }

        await AuditAsync(
            "upload.complete",
            $"upload/{uploadId}",
            AuditResult.Success,
            StatusCodes.Status201Created,
            new { fileId = file.Id, file.Size },
            cancellationToken);

        return Created($"/api/files/{file.Id}", FileMetadataResponse.FromEntity(file));
    }

    [HttpDelete("{uploadId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway, "application/problem+json")]
    public async Task<IActionResult> Abort(
        Guid uploadId,
        CancellationToken cancellationToken)
    {
        try
        {
            await uploads.AbortAsync(uploadId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await AuditFailureAsync("upload.abort", $"upload/{uploadId}", exception, cancellationToken);
            throw;
        }

        await AuditAsync(
            "upload.abort",
            $"upload/{uploadId}",
            AuditResult.Success,
            StatusCodes.Status204NoContent,
            null,
            cancellationToken);

        return NoContent();
    }

    [HttpPost("{uploadId:guid}/abort")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway, "application/problem+json")]
    public async Task<IActionResult> AbortPost(
        Guid uploadId,
        CancellationToken cancellationToken)
    {
        try
        {
            await uploads.AbortAsync(uploadId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await AuditFailureAsync("upload.abort", $"upload/{uploadId}", exception, cancellationToken);
            throw;
        }

        await AuditAsync(
            "upload.abort",
            $"upload/{uploadId}",
            AuditResult.Success,
            StatusCodes.Status204NoContent,
            null,
            cancellationToken);

        return NoContent();
    }

    private MultipartUploadResponse ToResponse(
        Reon.Core.Entities.MultipartUpload upload,
        IReadOnlyList<Reon.Core.Entities.MultipartUploadPart> parts)
    {
        return new MultipartUploadResponse(
            upload.Id,
            upload.FileId,
            upload.Status.ToString(),
            options.Value.MultipartPartSizeBytes,
            options.Value.MinMultipartPartSizeBytes,
            options.Value.MaxMultipartParts,
            upload.CreatedAt,
            upload.ExpiresAt,
            upload.CompletedAt,
            parts
                .OrderBy(part => part.PartNumber)
                .Select(part => new MultipartPartResponse(
                    part.PartNumber,
                    part.ETag,
                    part.Size,
                    part.UploadedAt))
                .ToArray());
    }
}
