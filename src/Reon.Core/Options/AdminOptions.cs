namespace Reon.Core.Options;

public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    // Сколько секунд кешируется рассчитанное использование бакета.
    public int UsageCacheSeconds { get; set; } = 60;

    // Верхняя граница размера страницы на административных списках.
    public int MaxPageSize { get; set; } = 200;

    // Максимальный срок жизни presigned URL (ограничение S3 — 7 дней).
    public int MaxPresignedUrlLifetimeSeconds { get; set; } = 604_800;

    // Верхняя граница числа объектов при поиске по подстроке.
    // В S3 нет нативного поиска, поэтому сервис фильтрует объекты в памяти.
    public int MaxSearchObjects { get; set; } = 5_000;

    // Актор, который записывается, если вызывающий не передал X-Reon-Actor.
    public string DefaultActor { get; set; } = "system";

    // Интервал heartbeat-кадров сервера на /api/v1/admin/events в секундах.
    public int EventHeartbeatSeconds { get; set; } = 30;
}
