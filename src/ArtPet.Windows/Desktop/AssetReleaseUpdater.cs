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
    private const string ArchiveName = "ArtPet-assets.zip";
    private const string LegacyArchiveName = "ArtPet-hoolheyak-assets.zip";
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

        if (GitHubReleaseSource.FindPackage(Repository, release, ManagedCharacterAssets.ManifestName) is not { Size: <= 16 * 1024 } metadata)
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

        string archiveName = manifest.IsCatalog ? ArchiveName : LegacyArchiveName;
        if (GitHubReleaseSource.FindPackage(Repository, release, archiveName) is not { Size: <= MaximumArchiveSize } package)
        {
            return new(null, "GitHub 尚未提供可驗證的美術資源包。");
        }

        return new(new(manifest, package), $"找到美術資源 {version}。");
    }

    internal static async Task InstallAsync(AssetReleaseUpdate update, AssetInstallation? running,
        IProgress<string> progress, CancellationToken token)
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
            ExtractPackage(archive, payload, update.Manifest.SchemaVersion, token);
            if (update.Manifest.IsCatalog)
            {
                await PrepareCatalogAsync(payload, progress, token);
            }
            else
            {
                if (update.Manifest.SchemaVersion == SpineFrameCache.SchemaVersion)
                {
                    progress.Report("正在本機產生並驗證動畫影格…");
                    string generated = Path.Combine(staging, "generated");
                    await SpineFrameCache.GenerateAsync(payload, generated, ManagedCharacterAssets.LegacyPackId, token);
                    payload = generated;
                }
                if (CharacterPack.TryOpen(payload) is not { Id: ManagedCharacterAssets.LegacyPackId } pack)
                {
                    throw new InvalidDataException("美術資源包缺少有效的霍爾海雅角色清單。");
                }
                pack.ValidateArtwork(token);
            }

            AssetInstallation installed = ManagedCharacterAssets.Activate(payload, update.Manifest, running, pending, token);
            Volatile.Write(ref _pending, installed);
        }
        finally
        {
            ManagedCharacterAssets.TryDeleteDirectory(staging);
            InstallGate.Release();
        }
    }

    private static async Task PrepareCatalogAsync(string payload, IProgress<string> progress, CancellationToken token)
    {
        ArtworkCatalog catalog;
        try
        {
            catalog = ArtworkCatalog.Read(payload);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("無法讀取完整美術資源包的角色清單。", error);
        }

        ArtworkCatalog.LoadAppIcon(payload);
        for (int index = 0; index < catalog.Characters.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            ArtworkCharacter character = catalog.Characters[index];
            progress.Report($"正在準備角色美術資源（{index + 1}/{catalog.Characters.Length}）…");
            if (character.HasSpineSource)
            {
                await SpineFrameCache.GenerateAsync(character.SourceDirectory(payload), character.PlaybackDirectory(payload),
                    character.Id, token);
            }
            character.LoadValidated(payload, token);
        }
    }

    private static bool IsAllowedAsset(string path, int schemaVersion)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (schemaVersion == ArtworkCatalog.SchemaVersion)
        {
            if (path is ArtworkCatalog.CatalogName or ArtworkCatalog.IconName)
            {
                return true;
            }
            if (!path.StartsWith("characters\\", StringComparison.Ordinal) && !path.StartsWith("images\\", StringComparison.Ordinal))
            {
                return false;
            }
            return Path.GetFileName(path) == "character.json" && path.StartsWith("characters\\", StringComparison.Ordinal) ||
                extension is ".png" or ".jpg" or ".jpeg" or ".ico" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".webp" or ".atlas" or ".skel";
        }

        return path == "character.json" || extension is ".png" or ".jpg" or ".jpeg" ||
            schemaVersion == SpineFrameCache.SchemaVersion && extension is (".atlas" or ".skel");
    }

    private static void ExtractPackage(string archive, string payload, int schemaVersion, CancellationToken token)
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
            remaining -= entry.Length;
            if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !IsAllowedAsset(path, schemaVersion) ||
                (schemaVersion == SpineFrameCache.SchemaVersion && entry.Length > SpineFrameCache.MaximumSourceFileSize) ||
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
