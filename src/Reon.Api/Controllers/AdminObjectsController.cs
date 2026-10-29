namespace Reon.Api.Controllers;

[Route("api/v1/admin/buckets/{bucket}/objects")]
[Produces("application/json")]
public sealed class AdminObjectsController(
    IStorageAdminService admin,
    ITempFileFactory tempFiles,
    IOptions<FileStorageOptions> fileOptions,
    IStorageAuditor auditor,
    IOptions<AdminOptions> adminOptions) : ReonApiControllerBase(auditor, adminOptions)
{
    /// <summary>Список объектов и «папок» (общих префиксов) внутри префикса. В S3 нет поиска по подстроке: при <c>search</c> сервис просматривает до Admin:MaxSearchObjects объектов и фильтрует их в памяти.</summary>
    [HttpGet]
    [ProducesResponseType<ObjectListingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<ObjectListingResponse>> List(
        string bucket,
        [FromQuery] string? prefix = null,
        [FromQuery] string delimiter = "/",
        [FromQuery] string? search = null,
        [FromQuery] int maxKeys = 100,
        [FromQuery] string? continuationToken = null,
        [FromQuery] bool includeContentType = false,
        CancellationToken cancellationToken = default)
    {
        var listing = await admin.ListObjectsAsync(
            new ObjectListQuery(bucket, prefix, delimiter, search, maxKeys, continuationToken, includeContentType),
            cancellationToken);

        return Ok(new ObjectListingResponse(
            listing.Prefixes,
            listing.Objects.Select(item => item.ToResponse()).ToArray(),
            listing.NextContinuationToken));
    }

    /// <summary>Загружает один или несколько файлов в текущий префикс.</summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<BatchUploadResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<BatchUploadResponse>> Upload(
        string bucket,
        [FromForm] UploadObjectsRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Files is null || request.Files.Count == 0)
        {
            throw new StorageValidationException("Attach at least one file in the 'files' form field.");
        }

        var metadata = ParseMap(request.Metadata, "metadata");
        var tags = ParseMap(request.Tags, "tags");
        var prefix = NormalizePrefix(request.Prefix);

        var entries = request.Files
            .Select(file => new BatchUploadEntry(
                prefix + file.FileName,
                string.IsNullOrWhiteSpace(file.ContentType)
                    ? "application/octet-stream"
                    : file.ContentType,
                file.Length,
                token => ValueTask.FromResult<Stream>(file.OpenReadStream())))
            .ToArray();

        var uploaded = await admin.UploadBatchAsync(
            bucket,
            entries,
            metadata,
            tags,
            Actor,
            cancellationToken);

        return StatusCode(
            StatusCodes.Status201Created,
            new BatchUploadResponse(uploaded.Select(item => item.ToResponse()).ToArray()));
    }

    /// <summary>Загружает один объект телом запроса.</summary>
    [HttpPut("{**key}")]
    [ProducesResponseType<ObjectItemResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge, "application/problem+json")]
    public async Task<ActionResult<ObjectItemResponse>> UploadSingle(
        string bucket,
        string key,
        CancellationToken cancellationToken)
    {
        key = DecodePathKey(key);

        await using var temp = await tempFiles.CreateAsync(
            Request.Body,
            fileOptions.Value.MaxFileSizeBytes,
            cancellationToken);

        await using var stream = temp.OpenRead();

        var stored = await admin.UploadObjectAsync(
            new UploadObjectCommand(
                bucket,
                key,
                string.IsNullOrWhiteSpace(Request.ContentType)
                    ? "application/octet-stream"
                    : Request.ContentType,
                temp.Size),
            stream,
            Actor,
            cancellationToken);

        return Created(
            $"/api/v1/admin/buckets/{bucket}/objects/{Uri.EscapeDataString(stored.ObjectKey)}",
            stored.ToResponse());
    }

    /// <summary>Скачивает объект, при необходимости конкретную версию.</summary>
    [HttpGet("{**key}")]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK, "application/octet-stream")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway, "application/problem+json")]
    public async Task<IActionResult> Download(
        string bucket,
        string key,
        [FromQuery] string? versionId = null,
        CancellationToken cancellationToken = default)
    {
        key = DecodePathKey(key);
        var content = await admin.OpenObjectAsync(bucket, key, versionId, cancellationToken);

        if (!string.IsNullOrWhiteSpace(content.ETag))
        {
            Response.Headers.ETag = content.ETag;
        }

        if (!string.IsNullOrWhiteSpace(content.VersionId))
        {
            Response.Headers["X-Reon-Version-Id"] = content.VersionId;
        }

        return File(content.Stream, content.ContentType, enableRangeProcessing: false);
    }

    /// <summary>Заголовки объекта без тела.</summary>
    [HttpHead("{**key}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Head(
        string bucket,
        string key,
        [FromQuery] string? versionId = null,
        CancellationToken cancellationToken = default)
    {
        key = DecodePathKey(key);
        var info = await admin.GetObjectInfoAsync(bucket, key, versionId, cancellationToken);

        Response.ContentType = info.ContentType ?? "application/octet-stream";
        Response.ContentLength = info.Size;

        if (!string.IsNullOrWhiteSpace(info.ETag))
        {
            Response.Headers.ETag = info.ETag;
        }

        if (info.LastModified is { } lastModified)
        {
            Response.Headers.LastModified = lastModified.ToUniversalTime().ToString("R");
        }

        if (!string.IsNullOrWhiteSpace(info.VersionId))
        {
            Response.Headers["X-Reon-Version-Id"] = info.VersionId;
        }

        return Ok();
    }

    /// <summary>Удаляет объект (или одну версию, если указан versionId).</summary>
    [HttpDelete("{**key}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway, "application/problem+json")]
    public async Task<IActionResult> Delete(
        string bucket,
        string key,
        [FromQuery] string? versionId = null,
        CancellationToken cancellationToken = default)
    {
        await admin.DeleteObjectAsync(bucket, DecodePathKey(key), versionId, Actor, cancellationToken);
        return NoContent();
    }

    /// <summary>Метаданные объекта, пользовательские метаданные, теги и число версий.</summary>
    [HttpGet("{key}/metadata")]
    [ProducesResponseType<ObjectMetadataResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<ObjectMetadataResponse>> Metadata(
        string bucket,
        string key,
        [FromQuery] string? versionId = null,
        CancellationToken cancellationToken = default)
    {
        key = DecodeKey(key);
        var info = await admin.GetObjectInfoAsync(bucket, key, versionId, cancellationToken);
        var tags = await admin.GetTagsAsync(bucket, key, versionId, cancellationToken);
        var versionCount = await TryGetVersionCountAsync(bucket, key, cancellationToken);

        return Ok(new ObjectMetadataResponse(
            bucket,
            info.ObjectKey,
            info.Size,
            info.ContentType,
            info.ETag,
            info.VersionId,
            info.LastModified,
            info.Metadata,
            tags,
            versionCount));
    }

    /// <summary>Все версии объекта, включая delete markers.</summary>
    [HttpGet("{key}/versions")]
    [ProducesResponseType<ObjectVersionsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status501NotImplemented, "application/problem+json")]
    public async Task<ActionResult<ObjectVersionsResponse>> Versions(
        string bucket,
        string key,
        CancellationToken cancellationToken)
    {
        var versions = await admin.ListVersionsAsync(bucket, DecodeKey(key), cancellationToken);
        return Ok(new ObjectVersionsResponse(versions.Select(version => version.ToResponse()).ToArray()));
    }

    /// <summary>Удаляет конкретную версию объекта.</summary>
    [HttpDelete("{key}/versions/{versionId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> DeleteVersion(
        string bucket,
        string key,
        string versionId,
        CancellationToken cancellationToken)
    {
        await admin.DeleteVersionAsync(bucket, DecodeKey(key), versionId, Actor, cancellationToken);
        return NoContent();
    }

    /// <summary>Теги объекта — отдельная сущность, не путать с пользовательскими метаданными.</summary>
    [HttpGet("{key}/tags")]
    [ProducesResponseType<Dictionary<string, string>>(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<Dictionary<string, string>>> GetTags(
        string bucket,
        string key,
        [FromQuery] string? versionId = null,
        CancellationToken cancellationToken = default)
    {
        var tags = await admin.GetTagsAsync(bucket, DecodeKey(key), versionId, cancellationToken);
        return Ok(tags);
    }

    /// <summary>Заменяет набор тегов объекта.</summary>
    [HttpPut("{key}/tags")]
    [ProducesResponseType<Dictionary<string, string>>(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<Dictionary<string, string>>> SetTags(
        string bucket,
        string key,
        [FromBody] SetObjectTagsRequest request,
        [FromQuery] string? versionId = null,
        CancellationToken cancellationToken = default)
    {
        await admin.SetTagsAsync(
            bucket,
            DecodeKey(key),
            versionId,
            request.Tags,
            Actor,
            cancellationToken);

        return Ok(request.Tags);
    }

    /// <summary>Создаёт временную подписанную ссылку на прямое скачивание.</summary>
    [HttpPost("{key}/presigned-urls")]
    [ProducesResponseType<PresignedUrlResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<PresignedUrlResponse>> Presign(
        string bucket,
        string key,
        [FromBody] PresignedUrlRequest request,
        [FromQuery] string? versionId = null,
        CancellationToken cancellationToken = default)
    {
        var url = await admin.CreatePresignedUrlAsync(
            bucket,
            DecodeKey(key),
            versionId,
            TimeSpan.FromSeconds(request.ExpiresInSeconds),
            Actor,
            cancellationToken);

        return Ok(new PresignedUrlResponse(url.Url, url.ExpiresAt, url.Method));
    }

    private async Task<int> TryGetVersionCountAsync(
        string bucket,
        string key,
        CancellationToken cancellationToken)
    {
        try
        {
            var versions = await admin.ListVersionsAsync(bucket, key, cancellationToken);
            return versions.Count;
        }
        catch (StorageCapabilityNotSupportedException)
        {
            return 0;
        }
    }

    private static Dictionary<string, string>? ParseMap(string? json, string field)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (JsonException)
        {
            throw new StorageValidationException(
                $"Form field '{field}' must be a JSON object with string values.");
        }
    }

    private static string DecodeKey(string key) => Uri.UnescapeDataString(key);

    /// <summary>
    /// Значение catch-all-маршрута может прийти с закодированным слэшем,
    /// если сгенерированный клиент экранирует весь ключ. Обычные слэши и так
    /// работают.
    /// </summary>
    private static string DecodePathKey(string key) =>
        key.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return string.Empty;
        }

        var normalized = prefix.Trim().TrimStart('/');
        return normalized.EndsWith('/') ? normalized : normalized + "/";
    }
}
