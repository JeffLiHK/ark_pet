using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ArtPet.Windows.Desktop;

internal static class GitHubReleaseSource
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static long _apiRetryAfter;

    static GitHubReleaseSource()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("ArtPet-Windows");
        Client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    internal static async Task<GitHubRelease?> GetLatestAsync(string repository, CancellationToken token,
        params string[] fallbackAssets)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (fallbackAssets.Length > 0 && Volatile.Read(ref _apiRetryAfter) > now.ToUnixTimeSeconds())
        {
            return await GetLatestFromReleasePageAsync(repository, fallbackAssets, token);
        }

        using HttpResponseMessage response = await Client.GetAsync($"https://api.github.com/repos/{repository}/releases/latest", token);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (fallbackAssets.Length > 0 && (response.StatusCode == HttpStatusCode.TooManyRequests ||
            (response.StatusCode == HttpStatusCode.Forbidden &&
                (response.Headers.RetryAfter is not null ||
                    (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0"))))))
        {
            long retryAfter = (response.Headers.RetryAfter?.Date ??
                now + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1))).ToUnixTimeSeconds();
            if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resets) &&
                long.TryParse(resets.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out long reset))
            {
                retryAfter = Math.Max(retryAfter, reset);
            }
            Volatile.Write(ref _apiRetryAfter, retryAfter);
            return await GetLatestFromReleasePageAsync(repository, fallbackAssets, token);
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GitHubRelease>(token) ??
            throw new InvalidDataException("GitHub 發行資料無法辨識。");
    }

    private static async Task<GitHubRelease?> GetLatestFromReleasePageAsync(string repository, string[] assetNames,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, $"https://github.com/{repository}/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        using HttpResponseMessage release = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (release.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        release.EnsureSuccessStatusCode();

        string tagPrefix = $"/{repository}/releases/tag/";
        if (release.RequestMessage?.RequestUri is not { Scheme: "https", Host: "github.com", IsDefaultPort: true } page ||
            !page.AbsolutePath.StartsWith(tagPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("無法從 GitHub 發行頁確認最新版本。");
        }
        string tag = Uri.UnescapeDataString(page.AbsolutePath[tagPrefix.Length..]);
        if (tag is not { Length: > 1 } || tag[0] != 'v' || !Version.TryParse(tag[1..], out _))
        {
            throw new InvalidDataException("GitHub 發行頁的版本格式無法辨識。");
        }

        string encodedTag = Uri.EscapeDataString(tag);
        using var assetsRequest = new HttpRequestMessage(HttpMethod.Get,
            $"https://github.com/{repository}/releases/expanded_assets/{encodedTag}");
        assetsRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        using HttpResponseMessage assets = await Client.SendAsync(assetsRequest, token);
        assets.EnsureSuccessStatusCode();
        string markup = await assets.Content.ReadAsStringAsync(token);
        Match[] rows = Regex.Matches(markup, @"<li\b[^>]*>.*?</li>",
            RegexOptions.Singleline | RegexOptions.NonBacktracking).Cast<Match>().ToArray();
        GitHubAsset?[] packages = await Task.WhenAll(assetNames.Select(name => ReadAssetAsync(name)));
        if (packages.All(asset => asset is null))
        {
            throw new InvalidDataException("GitHub 發行頁沒有可驗證的附件。");
        }
        return new(tag, packages.OfType<GitHubAsset>().ToArray());

        async Task<GitHubAsset?> ReadAssetAsync(string name)
        {
            string path = $"/{repository}/releases/download/{encodedTag}/{Uri.EscapeDataString(name)}";
            Match? row = rows.FirstOrDefault(row => row.Value.Contains($"href=\"{path}\"", StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                return null;
            }
            Match digest = Regex.Match(row.Value, @"\bvalue=""(sha256:[0-9a-fA-F]{64})""", RegexOptions.NonBacktracking);
            if (!digest.Success)
            {
                return null;
            }

            string url = "https://github.com" + path;
            try
            {
                using var head = new HttpRequestMessage(HttpMethod.Head, url);
                using HttpResponseMessage download = await Client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, token);
                return download.IsSuccessStatusCode && download.Content.Headers.ContentLength is > 0 and var size
                    ? new(name, size, digest.Groups[1].Value, url) : null;
            }
            catch (Exception error) when (!token.IsCancellationRequested &&
                error is HttpRequestException or OperationCanceledException)
            {
                // A missing optional attachment must not block the main package.
                return null;
            }
        }
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

    internal static async Task DownloadAsync(ReleasePackage package, Stream destination, CancellationToken token,
        Action<int>? reportPercentage = null)
    {
        using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        downloadTimeout.CancelAfter(TimeSpan.FromMinutes(15));
        CancellationToken downloadToken = downloadTimeout.Token;
        reportPercentage?.Invoke(0);
        using HttpResponseMessage response = await Client.GetAsync(package.DownloadUri, HttpCompletionOption.ResponseHeadersRead, downloadToken);
        response.EnsureSuccessStatusCode();
        await using Stream source = await response.Content.ReadAsStreamAsync(downloadToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        long remaining = package.Size;
        int lastPercentage = 0;
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
            if (reportPercentage is not null)
            {
                int percentage = (int)((package.Size - remaining) * 100d / package.Size);
                if (percentage > lastPercentage)
                {
                    lastPercentage = percentage;
                    reportPercentage(percentage);
                }
            }
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
