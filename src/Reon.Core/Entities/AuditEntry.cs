using Reon.Core.Enums;

namespace Reon.Core.Entities;

/// <summary>
/// Одна административная операция или операция с хранилищем, записанная
/// сервисом. Журнал ведётся только на добавление: записи не изменяются.
/// </summary>
public sealed class AuditEntry
{
    private AuditEntry()
    {
        // Требуется для EF Core.
    }

    public Guid Id { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public string Actor { get; private set; } = string.Empty;

    /// <summary>Стабильное машинное имя действия, например "object.upload".</summary>
    public string Action { get; private set; } = string.Empty;

    /// <summary>Затронутый ресурс: имя бакета или "bucket/key".</summary>
    public string Resource { get; private set; } = string.Empty;

    public AuditResult Result { get; private set; }

    public int? StatusCode { get; private set; }

    public JsonDocument? Details { get; private set; }

    public static AuditEntry Create(
        Guid id,
        string actor,
        string action,
        string resource,
        AuditResult result,
        int? statusCode,
        JsonDocument? details,
        DateTimeOffset now)
    {
        return new AuditEntry
        {
            Id = id,
            OccurredAt = now,
            Actor = actor,
            Action = action,
            Resource = resource,
            Result = result,
            StatusCode = statusCode,
            Details = details,
        };
    }
}
