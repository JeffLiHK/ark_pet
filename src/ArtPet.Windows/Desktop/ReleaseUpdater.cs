using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ArtPet.Windows.Desktop;

internal sealed record ReleaseUpdate(Version Version, Uri DownloadUri, byte[] Sha256, long Size);
internal sealed record ReleaseCheck(ReleaseUpdate? Update, string Message);

internal static class ReleaseUpdater
{
    private const string Repository = "JeffLiHK/art_pet";
    private const string AssetName = "ArtPet-win-x64.zip";
    private const string ExecutableName = "ArtPet.Windows.exe";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

    static ReleaseUpdater()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("ArtPet-Windows");
        Client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    internal static Version CurrentVersion { get; } = typeof(ReleaseUpdater).Assembly.GetName().Version!;

    internal static bool CanInstall => StartupRegistration.CanRegister &&
        RuntimeInformation.ProcessArchitecture == Architecture.X64 &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, "coreclr.dll"));

    internal static async Task<ReleaseCheck> CheckAsync(CancellationToken token)
    {
        using HttpResponseMessage response = await Client.GetAsync(
            $"https://api.github.com/repos/{Repository}/releases/latest", token);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new(null, "GitHub 目前沒有可公開讀取的正式發行版本。");
        }

        response.EnsureSuccessStatusCode();
        GitHubRelease? release = await response.Content.ReadFromJsonAsync<GitHubRelease>(token);
        if (release?.Tag is not { } tag || ReadTagVersion(tag) is not { } version)
        {
            return new(null, "GitHub 發行標籤的版本格式無法辨識。");
        }

        if (version <= CurrentVersion)
        {
            return new(null, "已是最新版本。");
        }

        GitHubAsset? asset = release.Assets?.FirstOrDefault(asset => asset.Name == AssetName);
        if (asset is not { Size: > 0, Digest: { Length: 71 } digest } ||
            !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
            !digest[7..].All(char.IsAsciiHexDigit) ||
            !Uri.TryCreate(asset.Download, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" ||
            !uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.EndsWith("/" + AssetName, StringComparison.Ordinal))
        {
            return new(null, $"已發行 {version.ToString(3)}，但 GitHub 尚未提供可驗證的 Windows x64 更新包。");
        }

        string message = CanInstall ? $"發現新版本 {version.ToString(3)}。" :
            $"發現新版本 {version.ToString(3)}。請使用 Windows x64 發佈版安裝更新。";
        return new(new(version, uri, Convert.FromHexString(digest[7..]), asset.Size), message);
    }

    private static Version? ReadTagVersion(string tag)
    {
        const string releasePrefix = "Release";
        string number = tag.StartsWith(releasePrefix, StringComparison.OrdinalIgnoreCase)
            ? tag[releasePrefix.Length..]
            : tag.TrimStart('v', 'V');
        return Version.TryParse(number, out Version? version) && version.Revision <= 0
            ? new Version(version.Major, version.Minor, Math.Max(0, version.Build), 0)
            : null;
    }

    internal static async Task InstallAsync(ReleaseUpdate update, CancellationToken token)
    {
        if (!CanInstall)
        {
            throw new InvalidOperationException("A self-contained win-x64 build is required.");
        }

        string target = AppContext.BaseDirectory;
        string staging = Path.Combine(target, $".artpet-update-{Guid.NewGuid():N}");
        bool installerStarted = false;
        try
        {
            Directory.CreateDirectory(staging);
            string archive = Path.Combine(staging, AssetName);
            using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            downloadTimeout.CancelAfter(TimeSpan.FromMinutes(15));
            using (HttpResponseMessage response = await Client.GetAsync(
                update.DownloadUri, HttpCompletionOption.ResponseHeadersRead, downloadTimeout.Token))
            {
                response.EnsureSuccessStatusCode();
                await using Stream source = await response.Content.ReadAsStreamAsync(downloadTimeout.Token);
                await using FileStream file = File.Create(archive);
                byte[] buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, downloadTimeout.Token)) > 0)
                {
                    total += read;
                    if (total > update.Size)
                    {
                        throw new InvalidDataException("更新包大小與 GitHub 發行資料不符。");
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), downloadTimeout.Token);
                }

                if (total != update.Size)
                {
                    throw new InvalidDataException("更新包下載不完整。");
                }
            }

            await Task.Run(() => PreparePackage(archive, staging, update, token), token);
            token.ThrowIfCancellationRequested();
            StartInstaller(staging, target);
            installerStarted = true;
        }
        finally
        {
            if (!installerStarted)
            {
                try
                {
                    Directory.Delete(staging, recursive: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static void PreparePackage(string archive, string staging, ReleaseUpdate update, CancellationToken token)
    {
        using (FileStream file = File.OpenRead(archive))
        {
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(file), update.Sha256))
            {
                throw new InvalidDataException("更新包的 SHA-256 驗證失敗。");
            }
        }

        string payload = Path.Combine(staging, "payload");
        Directory.CreateDirectory(payload);
        string payloadPrefix = payload + Path.DirectorySeparatorChar;
        using ZipArchive zip = ZipFile.OpenRead(archive);
        const string wrappedPackage = "ArtPet-win-x64\\";
        bool wrapped = zip.Entries.Any(entry =>
            entry.FullName.Replace('/', '\\').Equals(wrappedPackage + ExecutableName, StringComparison.OrdinalIgnoreCase));
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            string relative = entry.FullName.Replace('/', '\\');
            if (wrapped)
            {
                if (!relative.StartsWith(wrappedPackage, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("更新包的檔案須放在同一個發佈資料夾。");
                }

                relative = relative[wrappedPackage.Length..];
            }

            if (relative.Length == 0)
            {
                continue;
            }

            string topLevel = relative.Split('\\', 2)[0];
            string destination = Path.GetFullPath(Path.Combine(payload, relative));
            if (!destination.StartsWith(payloadPrefix, StringComparison.OrdinalIgnoreCase) ||
                relative.Contains(':') || relative.Split('\\').Any(part =>
                    part.EndsWith(' ') || part.EndsWith('.') || part is "." or "..") ||
                topLevel.StartsWith(".artpet-update-", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("更新包含有不支援的檔案路徑。");
            }

            if (!relative.EndsWith('\\') &&
                !topLevel.Equals("local-data", StringComparison.OrdinalIgnoreCase) &&
                !topLevel.Equals("assets", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination);
            }
        }

        if (!File.Exists(Path.Combine(payload, ExecutableName)) ||
            !File.Exists(Path.Combine(payload, "coreclr.dll")) ||
            !File.Exists(Path.Combine(payload, "ArtPet.Windows.runtimeconfig.json")) ||
            !File.Exists(Path.Combine(payload, "ArtPet.Windows.dll")) ||
            AssemblyName.GetAssemblyName(Path.Combine(payload, "ArtPet.Windows.dll")).Version != update.Version)
        {
            throw new InvalidDataException("更新包內容與 GitHub 發行版本不符。");
        }
    }

    private static void StartInstaller(string staging, string target)
    {
        using Stream resource = typeof(ReleaseUpdater).Assembly.GetManifestResourceStream("ArtPet.ApplyUpdate.ps1")!;
        using var reader = new StreamReader(resource);
        string command = Convert.ToBase64String(Encoding.Unicode.GetBytes(reader.ReadToEnd()));
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", command })
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["ARTPET_UPDATE_STAGE"] = staging;
        info.Environment["ARTPET_UPDATE_TARGET"] = target;
        info.Environment["ARTPET_UPDATE_PROCESS"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using Process installer = Process.Start(info) ?? throw new Win32Exception("Unable to start the updater.");
    }

    private sealed record GitHubRelease([property: JsonPropertyName("tag_name")] string? Tag, GitHubAsset[]? Assets);
    private sealed record GitHubAsset(string Name, long Size, string? Digest,
        [property: JsonPropertyName("browser_download_url")] string? Download);
}
