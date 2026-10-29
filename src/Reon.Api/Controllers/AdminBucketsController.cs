namespace Reon.Api.Controllers;

[Route("api/v1/admin/buckets")]
[Produces("application/json")]
public sealed class AdminBucketsController(
    IStorageAdminService admin,
    IStorageAuditor auditor,
    IOptions<AdminOptions> adminOptions) : ReonApiControllerBase(auditor, adminOptions)
{
    /// <summary>Список бакетов с версионированием, режимом доступа, квотой и использованием.</summary>
    [HttpGet]
    [ProducesResponseType<BucketListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway, "application/problem+json")]
    public async Task<ActionResult<BucketListResponse>> List(
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? sort = "name",
        CancellationToken cancellationToken = default)
    {
        var result = await admin.ListBucketsAsync(
            new BucketListQuery(search, page, pageSize, sort),
            cancellationToken);

        return Ok(result.ToResponse());
    }

    /// <summary>Создаёт бакет и его административные настройки.</summary>
    [HttpPost]
    [ProducesResponseType<BucketSummaryResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway, "application/problem+json")]
    public async Task<ActionResult<BucketSummaryResponse>> Create(
        [FromBody] CreateBucketRequest request,
        CancellationToken cancellationToken)
    {
        var bucket = await admin.CreateBucketAsync(
            new CreateBucketCommand(
                request.Name,
                request.VersioningEnabled,
                request.ObjectLockEnabled,
                request.QuotaBytes),
            Actor,
            cancellationToken);

        var response = bucket.ToResponse();
        return CreatedAtAction(nameof(Get), new { bucket = response.Name }, response);
    }

    /// <summary>Детали бакета: версионирование, Object Lock, шифрование, квота и использование.</summary>
    [HttpGet("{bucket}")]
    [ProducesResponseType<BucketSummaryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<BucketSummaryResponse>> Get(
        string bucket,
        [FromQuery] bool refreshUsage = false,
        CancellationToken cancellationToken = default)
    {
        var result = await admin.GetBucketAsync(bucket, refreshUsage, cancellationToken);
        return Ok(result.ToResponse());
    }

    /// <summary>Обновляет версионирование и квоту. Object Lock нельзя изменить после создания.</summary>
    [HttpPatch("{bucket}")]
    [ProducesResponseType<BucketSummaryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<BucketSummaryResponse>> Update(
        string bucket,
        [FromBody] UpdateBucketRequest request,
        CancellationToken cancellationToken)
    {
        var result = await admin.UpdateBucketAsync(
            bucket,
            new UpdateBucketCommand(
                request.VersioningEnabled,
                request.QuotaBytes,
                request.RemoveQuota),
            Actor,
            cancellationToken);

        return Ok(result.ToResponse());
    }

    /// <summary>Режим анонимного доступа и текущий документ bucket policy.</summary>
    [HttpGet("{bucket}/access")]
    [ProducesResponseType<BucketAccessResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<BucketAccessResponse>> GetAccess(
        string bucket,
        CancellationToken cancellationToken)
    {
        var access = await admin.GetBucketAccessAsync(bucket, cancellationToken);
        return Ok(new BucketAccessResponse(access.Mode.ToString(), access.Policy));
    }

    /// <summary>Устанавливает анонимный доступ: <c>private</c> или <c>public-read</c>. Публичное чтение реализуется bucket policy с правом s3:GetObject.</summary>
    [HttpPut("{bucket}/access")]
    [ProducesResponseType<BucketAccessResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status501NotImplemented, "application/problem+json")]
    public async Task<ActionResult<BucketAccessResponse>> SetAccess(
        string bucket,
        [FromBody] SetBucketAccessRequest request,
        CancellationToken cancellationToken)
    {
        var access = await admin.SetBucketAccessAsync(
            bucket,
            ParseAccessMode(request.Mode),
            Actor,
            cancellationToken);

        return Ok(new BucketAccessResponse(access.Mode.ToString(), access.Policy));
    }

    /// <summary>Правила lifecycle, настроенные на бакете.</summary>
    [HttpGet("{bucket}/lifecycle")]
    [ProducesResponseType<BucketLifecycleResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<BucketLifecycleResponse>> GetLifecycle(
        string bucket,
        CancellationToken cancellationToken)
    {
        var rules = await admin.GetLifecycleAsync(bucket, cancellationToken);
        return Ok(new BucketLifecycleResponse(rules.Select(rule => rule.ToResponse()).ToArray()));
    }

    /// <summary>Заменяет конфигурацию lifecycle бакета.</summary>
    [HttpPut("{bucket}/lifecycle")]
    [ProducesResponseType<BucketLifecycleResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status501NotImplemented, "application/problem+json")]
    public async Task<ActionResult<BucketLifecycleResponse>> SetLifecycle(
        string bucket,
        [FromBody] BucketLifecycleResponse request,
        CancellationToken cancellationToken)
    {
        var rules = request.Rules
            .Select(rule => new LifecycleRule(
                rule.Id,
                rule.Prefix,
                rule.Enabled,
                rule.ExpireAfterDays,
                rule.NoncurrentVersionExpireAfterDays,
                rule.AbortIncompleteMultipartUpload))
            .ToArray();

        var saved = await admin.SetLifecycleAsync(bucket, rules, Actor, cancellationToken);
        return Ok(new BucketLifecycleResponse(saved.Select(rule => rule.ToResponse()).ToArray()));
    }

    /// <summary>Текущее использование и квота одного бакета.</summary>
    [HttpGet("{bucket}/usage")]
    [ProducesResponseType<BucketUsageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<BucketUsageResponse>> GetUsage(
        string bucket,
        [FromQuery] bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var usage = await admin.GetUsageAsync(bucket, refresh, cancellationToken);
        return Ok(usage.ToResponse());
    }

    /// <summary>Пересчитывает использование по объектному хранилищу и рассылает событие.</summary>
    [HttpPost("{bucket}/usage/refresh")]
    [ProducesResponseType<BucketUsageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<BucketUsageResponse>> RefreshUsage(
        string bucket,
        CancellationToken cancellationToken)
    {
        var usage = await admin.RefreshUsageAsync(bucket, Actor, cancellationToken);
        return Ok(usage.ToResponse());
    }

    /// <summary>Создаёт пустой префикс-маркер, чтобы папка была видна в браузере объектов. Настоящих каталогов в S3 нет.</summary>
    [HttpPost("{bucket}/folders")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> CreateFolder(
        string bucket,
        [FromBody] CreateFolderRequest request,
        CancellationToken cancellationToken)
    {
        await admin.CreateFolderAsync(bucket, request.Prefix, request.Name, Actor, cancellationToken);

        var prefix = string.IsNullOrWhiteSpace(request.Prefix)
            ? string.Empty
            : request.Prefix.Trim().TrimStart('/').TrimEnd('/') + "/";

        return Created(
            $"/api/v1/admin/buckets/{bucket}/objects/{Uri.EscapeDataString(prefix + request.Name.Trim() + "/")}",
            new { bucket, key = prefix + request.Name.Trim() + "/" });
    }

    private static BucketAccessMode ParseAccessMode(string mode)
    {
        return mode?.Trim().ToLowerInvariant() switch
        {
            "private" => BucketAccessMode.Private,
            "public" or "publicread" or "public-read" or "download" => BucketAccessMode.PublicRead,
            _ => throw new StorageValidationException(
                "Mode must be 'private' or 'public-read'."),
        };
    }
}
