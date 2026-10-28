namespace Reon.Core.Enums;

/// <summary>
/// Lifecycle of a stored file. The enum is persisted as a string so that
/// the database stays readable and new states can be added without renumbering.
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
