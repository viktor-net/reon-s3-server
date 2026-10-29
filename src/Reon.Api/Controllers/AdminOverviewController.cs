namespace Reon.Api.Controllers;

[Route("api/v1/admin/overview")]
[Produces("application/json")]
public sealed class AdminOverviewController(
    IStorageAdminService admin,
    IStorageAuditor auditor,
    IOptions<AdminOptions> adminOptions) : ReonApiControllerBase(auditor, adminOptions)
{
    /// <summary>Сводка по хранилищу: бакеты, объекты, объём, публичные бакеты и использование по каждому бакету. Использование кешируется; <c>refreshUsage=true</c> пересчитывает его по объектному хранилищу.</summary>
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
