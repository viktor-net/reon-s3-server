namespace Reon.Core.Enums;

public static class BucketAccessModeExtensions
{
    /// <summary>
    /// Каноническое значение режима доступа для API: <c>private</c> или
    /// <c>public-read</c>. Enum-имена <c>Private</c>/<c>PublicRead</c> наружу
    /// не отдаются: фронтенд и OpenAPI работают с kebab-case.
    /// </summary>
    public static string ToApiValue(this BucketAccessMode mode)
    {
        return mode == BucketAccessMode.PublicRead ? "public-read" : "private";
    }

    /// <summary>
    /// Разбирает значение режима доступа. Поддерживает канонические значения,
    /// а также старые <c>Private</c>/<c>PublicRead</c> для обратной совместимости.
    /// </summary>
    public static bool TryParseApiValue(string? value, out BucketAccessMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "private":
                mode = BucketAccessMode.Private;
                return true;
            case "public":
            case "publicread":
            case "public-read":
            case "download":
                mode = BucketAccessMode.PublicRead;
                return true;
            default:
                mode = BucketAccessMode.Private;
                return false;
        }
    }
}
