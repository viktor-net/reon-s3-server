namespace Reon.Core.Enums;

/// <summary>
/// Источник защиты бакета от удаления. S3 Object Lock поддерживается не всеми
/// провайдерами, поэтому сервис умеет включать собственную защиту.
/// </summary>
public enum ObjectLockMode
{
    /// <summary>Защита не запрошена.</summary>
    None = 0,

    /// <summary>Настоящий S3 Object Lock, факт включения подтверждён провайдером.</summary>
    Provider = 1,

    /// <summary>Защита на уровне сервиса: удаление запрещено через API.</summary>
    Service = 2,
}
