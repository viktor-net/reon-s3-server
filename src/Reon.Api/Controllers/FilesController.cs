namespace Reon.Api.Controllers;

/// <summary>
/// Файловый API: загрузка, список, скачивание, метаданные, копирование и удаление.
/// </summary>
/// <param name="fileStorage">Сценарии файлового домена и жизненный цикл метаданных.</param>
/// <param name="auditor">Журнал операций: запись актора и результата каждой операции.</param>
/// <param name="adminOptions">Настройки: актор по умолчанию, если X-Reon-Actor не передан.</param>
[ApiController]
[Route("api/files")]
public sealed class FilesController(
    FileStorageService fileStorage,
    IStorageAuditor auditor,
    IOptions<AdminOptions> adminOptions) : ReonApiControllerBase(auditor, adminOptions)
{
    /// <summary>Загружает файл и возвращает его описание.</summary>
    /// <remarks>
    /// Срок хранения задаётся необязательным полем retentionSeconds в JSON
    /// metadata: это относительный срок в секундах, сервер сам вычисляет
    /// expiresAt. Если поле не передано, применяется
    /// FileStorage:DefaultRetentionDays (0 — бессрочно). Срок можно продлить
    /// отдельным запросом, а защитить файл от удаления — флагом retentionHold.
    /// </remarks>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [Produces("application/json")]
    [ProducesResponseType<FileMetadataResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway, "application/problem+json")]
    public async Task<ActionResult<FileMetadataResponse>> Upload(
        [FromForm] UploadFileRequest request,
        CancellationToken cancellationToken)
    {
        if (request.File is null || request.File.Length == 0)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Empty file",
                detail: "The multipart part 'file' must contain at least one byte.");
        }

        UploadMetadata? metadata;
        try
        {
            metadata = UploadMetadata.Parse(request.Metadata);
        }
        catch (System.Text.Json.JsonException)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid metadata",
                detail: "The 'metadata' form field must be a JSON object.");
        }

        var command = new UploadFileCommand(
            OriginalName: metadata?.OriginalName ?? request.File.FileName,
            ContentType: metadata?.ContentType ?? request.File.ContentType,
            Description: metadata?.Description,
            Tags: metadata?.Tags,
            RetentionSeconds: metadata?.RetentionSeconds);

        await using var stream = request.File.OpenReadStream();

        FileMetadata file;
        try
        {
            file = await fileStorage.UploadAsync(
                stream,
                command,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await AuditFailureAsync("file.upload", "files", exception, cancellationToken);
            throw;
        }

        await AuditAsync(
            "file.upload",
            $"file/{file.Id}",
            AuditResult.Success,
            StatusCodes.Status201Created,
            new { file.Size, file.ContentType },
            cancellationToken);

        var response = FileMetadataResponse.FromEntity(file);
        return Created($"/api/files/{file.Id}", response);
    }

    [HttpGet]
    [ProducesResponseType<FileListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<ActionResult<FileListResponse>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] FileStatus? status = null,
        [FromQuery] string? contentType = null,
        [FromQuery] string? originalName = null,
        [FromQuery] DateTimeOffset? createdFrom = null,
        [FromQuery] DateTimeOffset? createdTo = null,
        [FromQuery] string? sort = null,
        CancellationToken cancellationToken = default)
    {
        var result = await fileStorage.ListAsync(
            new FileListQuery(
                page,
                pageSize,
                status,
                contentType,
                originalName,
                createdFrom,
                createdTo,
                sort),
            cancellationToken);

        return Ok(new FileListResponse(
            result.Items.Select(FileMetadataResponse.FromEntity).ToArray(),
            result.Total,
            result.Page,
            result.PageSize));
    }

    /// <summary>Возвращает заголовки файла без тела.</summary>
    /// <remarks>
    /// Для просроченного файла возвращается 410, для неготового — 409.
    /// Заголовки Content-Type, Content-Length и ETag берутся из описания
    /// в Postgres.
    /// </remarks>
    [HttpHead("{fileId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status410Gone, "application/problem+json")]
    public async Task<IActionResult> Head(Guid fileId, CancellationToken cancellationToken)
    {
        var file = await fileStorage.GetAvailableMetadataAsync(fileId, cancellationToken);

        Response.ContentType = file.ContentType;
        Response.ContentLength = file.Size;

        if (!string.IsNullOrWhiteSpace(file.ETag))
        {
            Response.Headers.ETag = file.ETag;
        }

        return Ok();
    }

    [HttpPatch("{fileId:guid}/metadata")]
    [Produces("application/json")]
    [ProducesResponseType<FileMetadataResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<FileMetadataResponse>> UpdateMetadata(
        Guid fileId,
        [FromBody] UpdateFileMetadataRequest request,
        CancellationToken cancellationToken)
    {
        FileMetadata file;
        try
        {
            file = await fileStorage.UpdateMetadataAsync(
                fileId,
                request.OriginalName,
                request.Description,
                request.Tags,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await AuditFailureAsync("file.metadata.update", $"file/{fileId}", exception, cancellationToken);
            throw;
        }

        await AuditAsync(
            "file.metadata.update",
            $"file/{fileId}",
            AuditResult.Success,
            StatusCodes.Status200OK,
            null,
            cancellationToken);

        return Ok(FileMetadataResponse.FromEntity(file));
    }

    /// <summary>Меняет срок хранения или запрет на удаление.</summary>
    /// <remarks>
    /// retentionSeconds задаёт новый срок от текущего момента и может вернуть
    /// просроченный файл в Ready, если объект ещё не удалён. retentionHold
    /// запрещает или разрешает удаление по сроку. Оба поля необязательны;
    /// переданные значения применяются вместе.
    /// </remarks>
    [HttpPatch("{fileId:guid}/retention")]
    [Produces("application/json")]
    [ProducesResponseType<FileMetadataResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<FileMetadataResponse>> UpdateRetention(
        Guid fileId,
        [FromBody] UpdateRetentionRequest request,
        CancellationToken cancellationToken)
    {
        FileMetadata file;
        try
        {
            file = await fileStorage.UpdateRetentionAsync(
                fileId,
                request.RetentionSeconds,
                request.RetentionHold,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await AuditFailureAsync("file.retention.update", $"file/{fileId}", exception, cancellationToken);
            throw;
        }

        await AuditAsync(
            "file.retention.update",
            $"file/{fileId}",
            AuditResult.Success,
            StatusCodes.Status200OK,
            new { request.RetentionSeconds, request.RetentionHold },
            cancellationToken);

        return Ok(FileMetadataResponse.FromEntity(file));
    }

    /// <summary>Создаёт копию файла.</summary>
    /// <remarks>
    /// Копия получает собственный fileId и собственный срок хранения.
    /// retentionSeconds в теле задаёт его явно; если поле не передано,
    /// применяется FileStorage:DefaultRetentionDays, а не остаток срока
    /// исходного файла.
    /// </remarks>
    [HttpPost("{fileId:guid}/copy")]
    [Produces("application/json")]
    [ProducesResponseType<FileMetadataResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway, "application/problem+json")]
    public async Task<ActionResult<FileMetadataResponse>> Copy(
        Guid fileId,
        [FromBody] CopyFileRequest request,
        CancellationToken cancellationToken)
    {
        FileMetadata copy;
        try
        {
            copy = await fileStorage.CopyAsync(
                fileId,
                new CopyFileCommand(
                    request.OriginalName,
                    request.Description,
                    request.Tags,
                    request.RetentionSeconds),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await AuditFailureAsync("file.copy", $"file/{fileId}", exception, cancellationToken);
            throw;
        }

        await AuditAsync(
            "file.copy",
            $"file/{copy.Id}",
            AuditResult.Success,
            StatusCodes.Status201Created,
            new { sourceFileId = fileId },
            cancellationToken);

        return Created($"/api/files/{copy.Id}", FileMetadataResponse.FromEntity(copy));
    }

    [HttpPost("bulk-delete")]
    [ProducesResponseType<BulkDeleteResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<ActionResult<BulkDeleteResponse>> BulkDelete(
        [FromBody] BulkDeleteRequest request,
        CancellationToken cancellationToken)
    {
        var fileIds = request.FileIds?.Distinct().ToArray() ?? [];
        if (fileIds.Length == 0 || fileIds.Length > 100)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid fileIds",
                detail: "Provide between 1 and 100 unique fileIds.");
        }

        var results = new List<BulkDeleteItem>(fileIds.Length);

        foreach (var fileId in fileIds)
        {
            try
            {
                var outcome = await fileStorage.DeleteAsync(fileId, cancellationToken);
                var httpStatus = outcome == DeleteOutcome.InProgress
                    ? StatusCodes.Status202Accepted
                    : StatusCodes.Status204NoContent;

                results.Add(new BulkDeleteItem(fileId, outcome.ToString(), httpStatus, null));
            }
            catch (FileMetadataNotFoundException)
            {
                results.Add(new BulkDeleteItem(fileId, "NotFound", StatusCodes.Status404NotFound, "File not found."));
                if (!request.ContinueOnError)
                {
                    break;
                }
            }
            catch (ObjectStorageException exception)
            {
                results.Add(new BulkDeleteItem(fileId, "Failed", StatusCodes.Status502BadGateway, exception.Message));
                if (!request.ContinueOnError)
                {
                    break;
                }
            }
            catch (Exception exception)
            {
                results.Add(new BulkDeleteItem(fileId, "Failed", StatusCodes.Status500InternalServerError, exception.Message));
                if (!request.ContinueOnError)
                {
                    break;
                }
            }
        }

        await AuditAsync(
            "file.bulk-delete",
            "files",
            AuditResult.Success,
            StatusCodes.Status200OK,
            new
            {
                total = results.Count,
                failed = results.Count(item => item.Status == "Failed"),
            },
            cancellationToken);

        return Ok(new BulkDeleteResponse(results));
    }

    /// <summary>Скачивает файл бинарным потоком.</summary>
    /// <remarks>
    /// Файл отдаётся только в статусе Ready. Если истёк срок хранения,
    /// возвращается 410 FILE_EXPIRED, даже когда объект ещё не удалён из S3:
    /// проверка выполняется по expires_at, а не по факту работы сборщика.
    /// </remarks>
    [HttpGet("{fileId:guid}")]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK, "application/octet-stream")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status410Gone, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway, "application/problem+json")]
    public async Task<IActionResult> Download(Guid fileId, CancellationToken cancellationToken)
    {
        var content = await fileStorage.OpenContentAsync(
            fileId,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(content.ETag))
        {
            Response.Headers.ETag = content.ETag;
        }

        Response.Headers["X-File-Id"] = fileId.ToString();
        Response.ContentLength = content.Length;

        return File(
            content.Stream,
            content.ContentType,
            content.FileName,
            enableRangeProcessing: false);
    }

    /// <summary>Возвращает описание файла.</summary>
    /// <remarks>
    /// Описание доступно и для просроченного файла: статус Expired и
    /// expiresAt позволяют UI показать, что срок истёк. Для удалённого файла
    /// возвращается 404.
    /// </remarks>
    [HttpGet("{fileId:guid}/metadata")]
    [Produces("application/json")]
    [ProducesResponseType<FileMetadataResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<FileMetadataResponse>> GetMetadata(
        Guid fileId,
        CancellationToken cancellationToken)
    {
        var file = await fileStorage.GetMetadataAsync(
            fileId,
            cancellationToken);

        return Ok(FileMetadataResponse.FromEntity(file));
    }

    [HttpDelete("{fileId:guid}")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway, "application/problem+json")]
    public async Task<IActionResult> Delete(Guid fileId, CancellationToken cancellationToken)
    {
        DeleteOutcome outcome;
        try
        {
            outcome = await fileStorage.DeleteAsync(fileId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await AuditFailureAsync("file.delete", $"file/{fileId}", exception, cancellationToken);
            throw;
        }

        var statusCode = outcome == DeleteOutcome.InProgress
            ? StatusCodes.Status202Accepted
            : StatusCodes.Status204NoContent;

        await AuditAsync(
            "file.delete",
            $"file/{fileId}",
            AuditResult.Success,
            statusCode,
            new { outcome = outcome.ToString() },
            cancellationToken);

        return outcome == DeleteOutcome.InProgress
            ? Accepted()
            : NoContent();
    }
}
