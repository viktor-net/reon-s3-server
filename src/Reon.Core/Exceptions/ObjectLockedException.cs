namespace Reon.Core.Exceptions;

/// <summary>
/// Удаление или перезапись защищённого объекта/бакета. Защита может быть
/// настоящим S3 Object Lock или сервисной блокировкой в Postgres.
/// </summary>
public sealed class ObjectLockedException : Exception
{
    public ObjectLockedException(string bucket, string? key, ObjectLockMode mode)
        : base(mode == ObjectLockMode.Provider
            ? $"Object Lock is enabled for bucket '{bucket}'; deletion is forbidden."
            : $"Deletion protection is enabled for bucket '{bucket}'; deletion through the API is forbidden.")
    {
        Bucket = bucket;
        Key = key;
        Mode = mode;
    }

    public string Bucket { get; }

    public string? Key { get; }

    public ObjectLockMode Mode { get; }
}
