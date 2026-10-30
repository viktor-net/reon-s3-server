namespace Reon.Core.Enums;

/// <summary>
/// Жизненный цикл сохранённого файла. Перечисление хранится строкой, чтобы
/// база оставалась читаемой, а новые состояния не требовали перенумерации.
/// </summary>
public enum FileStatus
{
    Pending = 0,
    Ready = 1,
    Deleting = 2,
    Deleted = 3,
    Failed = 4,
    Missing = 5,
    Corrupted = 6,

    /// <summary>Срок хранения истёк: содержимое ещё может лежать в S3 до очистки,
    /// но API уже не отдаёт файл и отвечает 410.</summary>
    Expired = 7,
}
