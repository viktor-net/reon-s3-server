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
