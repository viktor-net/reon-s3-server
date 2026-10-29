namespace Reon.Core.Options;

public sealed class FileStorageOptions
{
    public const string SectionName = "FileStorage";

    /// <summary>Значение владельца для файлов, пока клиентская аутентификация отключена.</summary>
    public string DefaultOwner { get; set; } = "system";

    /// <summary>Максимальный размер одного загружаемого файла. По умолчанию: 1 ГиБ.</summary>
    public long MaxFileSizeBytes { get; set; } = 1_073_741_824;

    /// <summary>Файлы больше этого размера загружаются через S3 параллельной загрузкой
    /// файла по частям (multipart-загрузкой). По умолчанию: 64 МиБ.</summary>
    public long MultipartThresholdBytes { get; set; } = 67_108_864;

    /// <summary>Размер части параллельной загрузки файла по частям
    /// (multipart-загрузки). S3 требует не меньше 5 МиБ. По умолчанию: 16 МиБ.</summary>
    public int MultipartPartSizeBytes { get; set; } = 16_777_216;

    /// <summary>Минимальный размер части, кроме последней. S3 требует 5 МиБ.</summary>
    public int MinMultipartPartSizeBytes { get; set; } = 5_242_880;

    /// <summary>Максимальное число частей в одной параллельной загрузке
    /// файла по частям (multipart-загрузке).</summary>
    public int MaxMultipartParts { get; set; } = 10_000;

    /// <summary>Сколько хранится незавершённая параллельная загрузка файла
    /// по частям (multipart-загрузка) до очистки. По умолчанию: 7 дней.</summary>
    public int MultipartUploadLifetimeDays { get; set; } = 7;

    /// <summary>Как часто прерываются просроченные параллельные загрузки
    /// файла по частям (multipart-загрузки).</summary>
    public int MultipartCleanupIntervalMinutes { get; set; } = 60;

    /// <summary>Каждый сгенерированный ключ объекта начинается с этого префикса.</summary>
    public string KeyPrefix { get; set; } = "files/";

    /// <summary>Строка Pending старше этого возраста проверяется сверкой по S3.</summary>
    public int PendingTimeoutMinutes { get; set; } = 60;

    /// <summary>Как часто повторяется попытка удаления для строки Deleting.</summary>
    public int DeletingRetryMinutes { get; set; } = 5;

    /// <summary>Интервал фоновой сверки.</summary>
    public int ReconciliationIntervalMinutes { get; set; } = 5;

    public bool OrphanSweepEnabled { get; set; } = true;

    public int OrphanSweepIntervalMinutes { get; set; } = 360;

    /// <summary>Объект без строки метаданных удаляется, только если он старше этого периода.</summary>
    public int OrphanGracePeriodHours { get; set; } = 24;

    /// <summary>Мягко удалённые строки очищаются через столько дней. 0 — хранить вечно.</summary>
    public int DeletedRetentionDays { get; set; } = 30;

    public int MaxTags { get; set; } = 50;

    public int MaxTagKeyLength { get; set; } = 64;

    public int MaxTagValueLength { get; set; } = 256;

    public int MaxDescriptionLength { get; set; } = 2000;

    public int MaxOriginalNameLength { get; set; } = 512;

    public int MaxContentTypeLength { get; set; } = 255;
}
