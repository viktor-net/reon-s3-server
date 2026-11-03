using System.Security.Cryptography;
using System.Text;

namespace Reon.Core.Services;

/// <summary>
/// Подписывает временные ссылки на скачивание, которые ведут на API,
/// а не на S3. Ссылка не раскрывает S3 endpoint и не требует доступа
/// клиента к объектному хранилищу.
/// </summary>
/// <remarks>
/// Подпись покрывает bucket, key, versionId и срок действия. Публичный
/// адрес API берётся из конфигурации (<c>Admin:PublicBaseUrl</c>), а не из
/// заголовка <c>Host</c> запроса: заголовок контролируется клиентом и не
/// должен влиять на подписанный URL.
/// </remarks>
public static class DownloadLinkSigner
{
    private const string Path = "/api/admin/download";

    public static string Create(
        string secret,
        string publicBaseUrl,
        string bucket,
        string key,
        string? versionId,
        DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicBaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var expires = expiresAt.ToUnixTimeSeconds();
        var signature = Compute(secret, bucket, key, versionId, expires);

        var query =
            $"bucket={Uri.EscapeDataString(bucket)}" +
            $"&key={Uri.EscapeDataString(key)}" +
            $"&expires={expires}" +
            $"&signature={Uri.EscapeDataString(signature)}";

        if (!string.IsNullOrEmpty(versionId))
        {
            query += $"&versionId={Uri.EscapeDataString(versionId)}";
        }

        return $"{publicBaseUrl.Trim().TrimEnd('/')}{Path}?{query}";
    }

    public static bool Validate(
        string secret,
        string bucket,
        string key,
        string? versionId,
        long expires,
        string signature,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(secret) ||
            string.IsNullOrWhiteSpace(bucket) ||
            string.IsNullOrWhiteSpace(key) ||
            string.IsNullOrWhiteSpace(signature) ||
            expires <= now.ToUnixTimeSeconds())
        {
            return false;
        }

        var expected = Compute(secret, bucket, key, versionId, expires);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signature));
    }

    private static string Compute(
        string secret,
        string bucket,
        string key,
        string? versionId,
        long expires)
    {
        var payload = $"{bucket}\n{key}\n{versionId ?? string.Empty}\n{expires}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));

        return Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
