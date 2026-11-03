namespace Reon.Core.Options;

public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    // Сколько секунд кешируется рассчитанное использование бакета.
    public int UsageCacheSeconds { get; set; } = 60;

    // Верхняя граница размера страницы на административных списках.
    public int MaxPageSize { get; set; } = 200;

    // Максимальный срок жизни временной ссылки на скачивание (7 дней).
    public int MaxPresignedUrlLifetimeSeconds { get; set; } = 604_800;

    // Публичный адрес API, на который подписываются временные ссылки.
    // Не берётся из заголовка Host: Host контролируется клиентом.
    public string PublicBaseUrl { get; set; } = "http://localhost:10007";

    // Секрет HMAC для временных ссылок на скачивание. Обязателен в production.
    public string DownloadLinkSecret { get; set; } = string.Empty;

    // Верхняя граница числа объектов при поиске по подстроке.
    // В S3 нет нативного поиска, поэтому сервис фильтрует объекты в памяти.
    public int MaxSearchObjects { get; set; } = 5_000;

    // Актор, который записывается, если вызывающий не передал X-Reon-Actor.
    public string DefaultActor { get; set; } = "system";

    // Интервал heartbeat-кадров сервера на /api/admin/events в секундах.
    public int EventHeartbeatSeconds { get; set; } = 30;
}
