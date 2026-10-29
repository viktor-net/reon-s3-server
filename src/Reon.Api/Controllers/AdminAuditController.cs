namespace Reon.Api.Controllers;

/// <summary>
/// Журнал операций: постраничное чтение записей административного и файлового API.
/// </summary>
/// <param name="admin">Сценарии фасада: выборка записей журнала с фильтрами.</param>
/// <param name="auditor">Журнал операций: база контроллера пишет в него актора и результат.</param>
/// <param name="adminOptions">Настройки фасада: актор по умолчанию и граница размера страницы.</param>
[Route("api/v1/admin/audit")]
[Produces("application/json")]
public sealed class AdminAuditController(
    IStorageAdminService admin,
    IStorageAuditor auditor,
    IOptions<AdminOptions> adminOptions) : ReonApiControllerBase(auditor, adminOptions)
{
    /// <summary>Журнал операций</summary>
    /// <remarks>Административные и файловые операции: актор, ресурс, результат и необязательные JSON-детали.</remarks>
    [HttpGet]
    [ProducesResponseType<AuditListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<ActionResult<AuditListResponse>> List(
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] string? actor = null,
        [FromQuery] string? action = null,
        [FromQuery] string? resource = null,
        [FromQuery] string? result = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        AuditResult? auditResult = null;

        if (!string.IsNullOrWhiteSpace(result))
        {
            if (!Enum.TryParse<AuditResult>(result, ignoreCase: true, out var parsed))
            {
                throw new StorageValidationException("Result must be 'Success' or 'Failure'.");
            }

            auditResult = parsed;
        }

        var auditPage = await admin.ListAuditAsync(
            new AuditQuery(from, to, actor, action, resource, auditResult, page, pageSize),
            cancellationToken);

        return Ok(auditPage.ToResponse());
    }
}
