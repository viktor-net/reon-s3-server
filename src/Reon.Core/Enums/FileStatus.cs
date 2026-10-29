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
}
