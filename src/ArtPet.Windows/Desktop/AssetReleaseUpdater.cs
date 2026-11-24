using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArtPet.Windows.Media;

namespace ArtPet.Windows.Desktop;

internal sealed record AssetReleaseUpdate(AssetReleaseManifest Manifest, ReleasePackage Package);
internal sealed record AssetReleaseCheck(AssetReleaseUpdate? Update, string Message);

internal static class AssetReleaseUpdater
{
    private const string Repository = "JeffLiHK/art_pet_assets";
    private const string ArchiveName = "ArtPet-hoolheyak-assets.zip";
    private const long MaximumArchiveSize = 512L * 1024 * 1024;
    private const long MaximumExtractedSize = 1024L * 1024 * 1024;
    private const int MaximumEntries = 20_000;
    private static readonly SemaphoreSlim InstallGate = new(1);
    private static AssetInstallation? _pending;

    internal static AssetInstallation? Pending => Volatile.Read(ref _pending);

    internal static async Task<AssetReleaseCheck> CheckAsync(AssetInstallation? running, CancellationToken token)
    {
        GitHubRelease? release = await GitHubReleaseSource.GetLatestAsync(Repository, token);
        if (release is null)
        {
            return new(null, "GitHub 目前沒有可下載的美術資源。");
        }

        if (release.Tag is not { Length: > 1 } tag || tag[0] != 'v' ||
            !Version.TryParse(tag[1..], out Version? version) || !AssetReleaseManifest.IsReleaseVersion(version))
        {
            return new(null, "美術資源的發行版本格式無法辨識。");
        }

        if (version <= Pending?.Version)
        {
            return new(null, "新版美術資源已安裝，將於下次啟動生效。");
        }

        if (version <= running?.Version)
        {
            return new(null, "已是最新美術資源。");
        }

        if (GitHubReleaseSource.FindPackage(Repository, release, ArchiveName) is not { Size: <= MaximumArchiveSize } package ||
            GitHubReleaseSource.FindPackage(Repository, release, ManagedCharacterAssets.ManifestName) is not { Size: <= 16 * 1024 } metadata)
        {
            return new(null, "GitHub 尚未提供可驗證的美術資源包和版本清單。");
        }

        using var contents = new MemoryStream();
        await GitHubReleaseSource.DownloadAsync(metadata, contents, token);
        contents.Position = 0;
        AssetReleaseManifest? manifest = await JsonSerializer.DeserializeAsync<AssetReleaseManifest>(
            contents, ManagedCharacterAssets.JsonOptions, token);
        if (manifest is not { IsValid: true } || manifest.AssetVersion != version)
        {
            return new(null, "美術資源版本清單與 GitHub 發行版本不符。");
        }

        if (manifest.MinAppVersion > ReleaseUpdater.CurrentVersion)
        {
            return new(null, $"美術資源 {version} 需要程式 {manifest.MinAppVersion} 或更新版本。");
        }

        if (!manifest.Supports(ReleaseUpdater.CurrentVersion))
        {
            return new(null, $"目前的程式版本無法讀取美術資源 {version}。");
        }

        return new(new(manifest, package), $"可下載霍爾海雅美術資源 {version}。");
    }

    internal static async Task InstallAsync(AssetReleaseUpdate update, AssetInstallation? running, CancellationToken token)
    {
        await InstallGate.WaitAsync(token);
        string staging = Path.Combine(ManagedCharacterAssets.RootDirectory, $".stage-{Guid.NewGuid():N}");
        try
        {
            AssetInstallation? pending = Pending;
            if (update.Manifest.AssetVersion <= running?.Version || update.Manifest.AssetVersion <= pending?.Version)
            {
                return;
            }

            Directory.CreateDirectory(staging);
            string archive = Path.Combine(staging, ArchiveName);
            await using (FileStream file = File.Create(archive))
            {
                await GitHubReleaseSource.DownloadAsync(update.Package, file, token);
            }

            string payload = Path.Combine(staging, "payload");
            ExtractPackage(archive, payload, token);
            if (CharacterPack.TryOpen(payload) is not { Id: ManagedCharacterAssets.PackId } pack)
            {
                throw new InvalidDataException("美術資源包缺少有效的霍爾海雅角色清單。");
            }

            pack.ValidateForInstall(token);
            AssetInstallation installed = ManagedCharacterAssets.Activate(payload, update.Manifest, running, pending, token);
            Volatile.Write(ref _pending, installed);
        }
        finally
        {
            ManagedCharacterAssets.TryDeleteDirectory(staging);
            InstallGate.Release();
        }
    }

    private static void ExtractPackage(string archive, string payload, CancellationToken token)
    {
        using ZipArchive zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > MaximumEntries)
        {
            throw new InvalidDataException("美術資源包的檔案數量超出上限。");
        }

        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        byte[] buffer = new byte[81920];
        string prefix = payload + Path.DirectorySeparatorChar;
        long remaining = MaximumExtractedSize;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            string relative = entry.FullName.Replace('/', '\\');
            bool isDirectory = relative.EndsWith('\\');
            string path = isDirectory ? relative[..^1] : relative;
            if (path.Split('\\').Any(part => part.Length == 0 || part.EndsWith(' ') || part.EndsWith('.') ||
                part.IndexOfAny(invalidCharacters) >= 0))
            {
                throw new InvalidDataException("美術資源包含有不支援的檔案路徑。");
            }

            if (isDirectory)
            {
                continue;
            }

            string destination = Path.GetFullPath(path, payload);
            string extension = Path.GetExtension(path).ToLowerInvariant();
            remaining -= entry.Length;
            if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                (path != "character.json" && extension is not (".png" or ".jpg" or ".jpeg")) ||
                remaining < 0)
            {
                throw new InvalidDataException("美術資源包含有不支援的檔案或超出大小上限。");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using Stream source = entry.Open();
            using FileStream target = File.Open(destination, FileMode.CreateNew);
            long bytesLeft = entry.Length;
            int read;
            while ((read = source.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                bytesLeft -= read;
                if (bytesLeft < 0)
                {
                    break;
                }

                target.Write(buffer.AsSpan(0, read));
            }

            if (bytesLeft != 0)
            {
                throw new InvalidDataException("美術資源包內的檔案大小與 ZIP 清單不符。");
            }
        }
    }
}
