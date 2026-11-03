using System.Text.RegularExpressions;

namespace Reon.UnitTests;

public sealed class DownloadLinkSignerTests
{
    private const string Secret = "test-download-link-secret";
    private const string BaseUrl = "http://localhost:10007";

    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_ProducesApiUrl_NotS3Url()
    {
        var url = DownloadLinkSigner.Create(
            Secret,
            BaseUrl,
            "exports",
            "dir/file.txt",
            versionId: null,
            Now.AddHours(1));

        Assert.StartsWith("http://localhost:10007/api/admin/download?", url);
        Assert.DoesNotContain("swift", url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("10001", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_AcceptsFreshLink()
    {
        var expiresAt = Now.AddHours(1);
        var url = DownloadLinkSigner.Create(
            Secret,
            BaseUrl,
            "exports",
            "dir/file.txt",
            versionId: null,
            expiresAt);

        var (expires, signature) = Parse(url);

        Assert.True(
            DownloadLinkSigner.Validate(
                Secret,
                "exports",
                "dir/file.txt",
                versionId: null,
                expires,
                signature,
                Now));
    }

    [Fact]
    public void Validate_RejectsExpiredLink()
    {
        var expiresAt = Now.AddMinutes(5);
        var url = DownloadLinkSigner.Create(
            Secret,
            BaseUrl,
            "exports",
            "dir/file.txt",
            versionId: null,
            expiresAt);

        var (expires, signature) = Parse(url);

        Assert.False(
            DownloadLinkSigner.Validate(
                Secret,
                "exports",
                "dir/file.txt",
                versionId: null,
                expires,
                signature,
                expiresAt.AddSeconds(1)));
    }

    [Fact]
    public void Validate_RejectsTamperedKey()
    {
        var url = DownloadLinkSigner.Create(
            Secret,
            BaseUrl,
            "exports",
            "dir/file.txt",
            versionId: null,
            Now.AddHours(1));

        var (expires, signature) = Parse(url);

        Assert.False(
            DownloadLinkSigner.Validate(
                Secret,
                "exports",
                "dir/other.txt",
                versionId: null,
                expires,
                signature,
                Now));
    }

    [Fact]
    public void Validate_BindsVersionId()
    {
        var url = DownloadLinkSigner.Create(
            Secret,
            BaseUrl,
            "exports",
            "dir/file.txt",
            versionId: "v1",
            Now.AddHours(1));

        var (expires, signature) = Parse(url);

        Assert.True(
            DownloadLinkSigner.Validate(
                Secret,
                "exports",
                "dir/file.txt",
                "v1",
                expires,
                signature,
                Now));

        Assert.False(
            DownloadLinkSigner.Validate(
                Secret,
                "exports",
                "dir/file.txt",
                "v2",
                expires,
                signature,
                Now));
    }

    private static (long Expires, string Signature) Parse(string url)
    {
        var expires = long.Parse(
            Regex.Match(url, @"[?&]expires=(\d+)").Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture);

        var signature = Uri.UnescapeDataString(
            Regex.Match(url, @"[?&]signature=([^&]+)").Groups[1].Value);

        return (expires, signature);
    }
}
