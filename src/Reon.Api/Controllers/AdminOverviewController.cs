namespace Reon.Api.Controllers;

/// <summary>
/// Обзор хранилища: агрегаты по бакетам и использование.
/// </summary>
/// <param name="admin">Сценарии фасада: сборка сводки по всем бакетам.</param>
/// <param name="auditor">Журнал операций: общая запись актора и результата.</param>
/// <param name="adminOptions">Настройки фасада: актор по умолчанию.</param>
[Route("api/v1/admin/overview")]
[Produces("application/json")]
public sealed class AdminOverviewController(
    IStorageAdminService admin,
    IStorageAuditor auditor,
    IOptions<AdminOptions> adminOptions) : ReonApiControllerBase(auditor, adminOptions)
{
    /// <summary>Обзор хранилища</summary>
    /// <remarks>Бакеты, объекты, объём, публичные бакеты и использование по каждому бакету. Использование кешируется; refreshUsage=true пересчитывает его по объектному хранилищу.</remarks>
    [HttpGet]
    [ProducesResponseType<AdminOverviewResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AdminOverviewResponse>> Get(
        [FromQuery] bool refreshUsage = false,
        CancellationToken cancellationToken = default)
    {
        var overview = await admin.GetOverviewAsync(refreshUsage, cancellationToken);
        return Ok(overview.ToResponse());
    }
}
