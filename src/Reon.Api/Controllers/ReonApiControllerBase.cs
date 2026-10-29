namespace Reon.Api.Controllers;

/// <summary>
/// Общая база контроллеров сервиса: определяет актора вызова и пишет журнал.
/// Клиентской аутентификации нет, поэтому актор берётся из заголовка
/// X-Reon-Actor, а при его отсутствии — из Admin:DefaultActor.
/// </summary>
[ApiController]
public abstract class ReonApiControllerBase(
    IStorageAuditor auditor,
    IOptions<AdminOptions> adminOptions) : ControllerBase
{
    private readonly IStorageAuditor _auditor = auditor;
    private readonly AdminOptions _adminOptions = adminOptions.Value;

    protected string Actor
    {
        get
        {
            if (Request.Headers.TryGetValue("X-Reon-Actor", out var values))
            {
                var actor = values.ToString().Trim();
                if (actor.Length > 0)
                {
                    return actor;
                }
            }

            return _adminOptions.DefaultActor;
        }
    }

    protected Task AuditAsync(
        string action,
        string resource,
        AuditResult result,
        int? statusCode,
        object? details,
        CancellationToken cancellationToken)
    {
        return _auditor.RecordAsync(
            Actor,
            action,
            resource,
            result,
            statusCode,
            details,
            cancellationToken);
    }

    protected Task AuditFailureAsync(
        string action,
        string resource,
        Exception exception,
        CancellationToken cancellationToken)
    {
        return AuditAsync(
            action,
            resource,
            AuditResult.Failure,
            statusCode: null,
            new { error = exception.Message },
            cancellationToken);
    }
}
