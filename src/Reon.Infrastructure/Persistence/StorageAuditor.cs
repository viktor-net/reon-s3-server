namespace Reon.Infrastructure.Persistence;

/// <summary>
/// Сохраняет записи аудита и дублирует их в поток административных событий.
/// Ошибки логируются и подавляются: аудит никогда не подменяет результат
/// записываемой операции.
/// </summary>
/// <param name="repository">Вставка записей в audit_events.</param>
/// <param name="events">Публикация события audit.appended.</param>
/// <param name="clock">Время события для записи и WebSocket-конверта.</param>
/// <param name="logger">Лог сбоев записи, которые не должны ломать операцию.</param>
public sealed partial class StorageAuditor(
    IAuditRepository repository,
    IStorageEventPublisher events,
    IClock clock,
    ILogger<StorageAuditor> logger) : IStorageAuditor
{
    public async Task RecordAsync(
        string actor,
        string action,
        string resource,
        AuditResult result,
        int? statusCode,
        object? details,
        CancellationToken cancellationToken)
    {
        try
        {
            var entry = AuditEntry.Create(
                Guid.CreateVersion7(),
                string.IsNullOrWhiteSpace(actor) ? "system" : actor.Trim(),
                action,
                resource,
                result,
                statusCode,
                details is null
                    ? null
                    : JsonSerializer.SerializeToDocument(details, JsonSerializerOptions.Web),
                clock.UtcNow);

            await repository.AddAsync(entry, cancellationToken);
            await repository.SaveChangesAsync(cancellationToken);

            await events.PublishAsync(
                new StorageEvent(
                    StorageEvent.AuditAppended,
                    entry.OccurredAt,
                    entry,
                    BucketFromResource(resource)),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogAuditFailed(logger, exception, action);
        }
    }

    private static string? BucketFromResource(string resource)
    {
        var separator = resource.IndexOf('/');
        return separator < 0 ? resource : resource[..separator];
    }

    [LoggerMessage(EventId = 2301, Level = LogLevel.Warning, Message = "Failed to persist an audit entry for action {Action}.")]
    private static partial void LogAuditFailed(ILogger logger, Exception exception, string action);
}
