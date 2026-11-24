using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ArtPet.Windows.Desktop;

internal static class GitHubReleaseSource
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

    static GitHubReleaseSource()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("ArtPet-Windows");
        Client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    internal static async Task<GitHubRelease?> GetLatestAsync(string repository, CancellationToken token)
    {
        using HttpResponseMessage response = await Client.GetAsync($"https://api.github.com/repos/{repository}/releases/latest", token);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GitHubRelease>(token) ??
            throw new InvalidDataException("GitHub 發行資料無法辨識。");
    }

    internal static ReleasePackage? FindPackage(string repository, GitHubRelease release, string name)
    {
        GitHubAsset? asset = release.Assets?.FirstOrDefault(asset => asset.Name == name);
        if (release.Tag is not { Length: > 0 } tag ||
            asset is not { Size: > 0, Digest: { Length: 71 } digest } ||
            !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
            !digest[7..].All(char.IsAsciiHexDigit) ||
            !Uri.TryCreate(asset.Download, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 ||
            !uri.AbsolutePath.Equals($"/{repository}/releases/download/{Uri.EscapeDataString(tag)}/{name}", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new ReleasePackage(uri, Convert.FromHexString(digest[7..]), asset.Size);
    }

    internal static async Task DownloadAsync(ReleasePackage package, Stream destination, CancellationToken token)
    {
        using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        downloadTimeout.CancelAfter(TimeSpan.FromMinutes(15));
        CancellationToken downloadToken = downloadTimeout.Token;
        using HttpResponseMessage response = await Client.GetAsync(package.DownloadUri, HttpCompletionOption.ResponseHeadersRead, downloadToken);
        response.EnsureSuccessStatusCode();
        await using Stream source = await response.Content.ReadAsStreamAsync(downloadToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        long remaining = package.Size;
        int read;
        while ((read = await source.ReadAsync(buffer, downloadToken)) > 0)
        {
            remaining -= read;
            if (remaining < 0)
            {
                throw new InvalidDataException("下載檔案大小與 GitHub 發行資料不符。");
            }

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), downloadToken);
        }

        if (remaining != 0)
        {
            throw new InvalidDataException("檔案下載不完整。");
        }

        if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), package.Sha256))
        {
            throw new InvalidDataException("下載檔案的 SHA-256 驗證失敗。");
        }
    }
}

internal sealed record ReleasePackage(Uri DownloadUri, byte[] Sha256, long Size);

internal sealed record GitHubRelease([property: JsonPropertyName("tag_name")] string? Tag, GitHubAsset[]? Assets);

internal sealed record GitHubAsset(string Name, long Size, string? Digest,
    [property: JsonPropertyName("browser_download_url")] string? Download);
