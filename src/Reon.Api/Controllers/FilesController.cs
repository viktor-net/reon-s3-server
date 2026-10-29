namespace Reon.Api.Controllers;

[ApiController]
[Route("api/v1/files")]
public sealed class FilesController(FileStorageService fileStorage) : ControllerBase
{
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
            Tags: metadata?.Tags);

        await using var stream = request.File.OpenReadStream();
        var file = await fileStorage.UploadAsync(
            stream,
            command,
            cancellationToken);

        var response = FileMetadataResponse.FromEntity(file);
        return Created($"/api/v1/files/{file.Id}", response);
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

    [HttpHead("{fileId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Head(Guid fileId, CancellationToken cancellationToken)
    {
        var file = await fileStorage.GetMetadataAsync(fileId, cancellationToken);
        if (file.Status != FileStatus.Ready)
        {
            throw new FileNotReadyException(file.Id, file.Status);
        }

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
        var file = await fileStorage.UpdateMetadataAsync(
            fileId,
            request.OriginalName,
            request.Description,
            request.Tags,
            cancellationToken);

        return Ok(FileMetadataResponse.FromEntity(file));
    }

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
        var copy = await fileStorage.CopyAsync(
            fileId,
            new CopyFileCommand(request.OriginalName, request.Description, request.Tags),
            cancellationToken);

        return Created($"/api/v1/files/{copy.Id}", FileMetadataResponse.FromEntity(copy));
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

        return Ok(new BulkDeleteResponse(results));
    }

    [HttpGet("{fileId:guid}")]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK, "application/octet-stream")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
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
        var outcome = await fileStorage.DeleteAsync(
            fileId,
            cancellationToken);

        return outcome == DeleteOutcome.InProgress
            ? Accepted()
            : NoContent();
    }
}
