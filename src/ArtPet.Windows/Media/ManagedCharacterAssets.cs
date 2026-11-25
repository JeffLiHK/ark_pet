using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Media.Imaging;

namespace ArtPet.Windows.Media;

internal sealed record AssetInstallation(Version Version, Guid Id);
internal sealed record AssetSnapshot(AssetInstallation Installation, CharacterPack[] Packs, BitmapSource? AppIcon,
    string? VoiceRoot = null);

internal sealed record AssetReleaseManifest(string PackId, Version AssetVersion, int SchemaVersion, Version MinAppVersion)
{
    internal bool IsCatalog => SchemaVersion >= ArtworkCatalog.SchemaVersion;
    internal bool HasVoiceAssets => SchemaVersion == ArtworkCatalog.VoiceSchemaVersion;
    internal bool HasSplitResources => SchemaVersion == ArtworkCatalog.SplitResourceSchemaVersion;

    internal bool IsValid => PackId == (IsCatalog ? ArtworkCatalog.PackId : ManagedCharacterAssets.LegacyPackId) &&
        IsReleaseVersion(AssetVersion) &&
        SchemaVersion > 0 && IsReleaseVersion(MinAppVersion);

    internal bool Supports(Version appVersion) =>
        SchemaVersion is (CharacterPack.SupportedSchemaVersion or SpineFrameCache.SchemaVersion or
            ArtworkCatalog.SchemaVersion or ArtworkCatalog.VoiceSchemaVersion or ArtworkCatalog.SplitResourceSchemaVersion) &&
        MinAppVersion <= appVersion;

    internal static bool IsReleaseVersion(Version? version) => version is { Build: >= 0, Revision: -1 };
}

internal static class ManagedCharacterAssets
{
    internal const string LegacyPackId = "hoolheyak";
    internal const string ManifestName = "assets-manifest.json";
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    // Keep the installation index readable across upgrades from the single-character release.
    internal static string RootDirectory { get; } = Path.Combine(ProjectStorage.ManagedAssetsDirectory, LegacyPackId);
    private static string StatePath => Path.Combine(RootDirectory, "active.json");

    internal static AssetSnapshot? LoadInstalled(Version appVersion)
    {
        AssetState state = ReadState();
        foreach (AssetInstallation? installation in new[] { state.Active, state.Previous }.Distinct())
        {
            if (installation is not { Id: var id } || id == Guid.Empty || !AssetReleaseManifest.IsReleaseVersion(installation.Version))
            {
                continue;
            }

            string directory = GetDirectory(installation);
            if (TryRead<AssetReleaseManifest>(Path.Combine(directory, ManifestName)) is not { IsValid: true } manifest ||
                manifest.AssetVersion != installation.Version || !manifest.Supports(appVersion))
            {
                continue;
            }

            try
            {
                if (manifest.IsCatalog)
                {
                    ArtworkCatalog catalog = ArtworkCatalog.Read(directory);
                    BitmapSource icon = ArtworkCatalog.LoadAppIcon(directory);
                    string? voiceRoot = null;
                    if (manifest.HasVoiceAssets || manifest.HasSplitResources)
                    {
                        VoiceAssets.Validate(directory, catalog, CancellationToken.None, subtitlesOnly: manifest.HasSplitResources);
                        voiceRoot = Path.Combine(directory, VoiceAssets.DirectoryName);
                    }
                    var packs = catalog.Characters.Where(character => character.Id != ArtworkCatalog.RetiredCharacterId)
                        .Select(character => manifest.HasSplitResources
                        ? character.OpenSource(directory, CancellationToken.None)
                        : character.LoadValidated(directory, CancellationToken.None)).ToArray();
                    return new(installation, packs,
                        catalog.Characters.Any(character => character.Id == ArtworkCatalog.RetiredCharacterId) ? null : icon,
                        voiceRoot);
                }

                if (CharacterPack.TryOpen(directory) is { Id: LegacyPackId } pack &&
                    pack.FindLoadableOutfit(CancellationToken.None) is not null)
                {
                    return new(installation, [pack], null);
                }
            }
            catch (Exception error) when (CharacterPack.IsAssetError(error) || error is JsonException)
            {
            }
        }
        return null;
    }

    internal static AssetInstallation Activate(string payload, AssetReleaseManifest manifest, AssetInstallation? running,
        AssetInstallation? pending, CancellationToken token)
    {
        using FileStream installLock = File.Open(Path.Combine(RootDirectory, ".install.lock"),
            FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        AssetState state = ReadState();
        if (state.Active?.Version > manifest.AssetVersion)
        {
            throw new InvalidDataException("電腦上已有較新的美術資源版本。");
        }

        var installation = new AssetInstallation(manifest.AssetVersion, Guid.NewGuid());
        AssetInstallation? previous = running ?? (state.Active == pending && pending is not null ? pending : state.Previous);
        string nextState = Path.Combine(Path.GetDirectoryName(payload)!, "active.json");
        File.WriteAllBytes(Path.Combine(payload, ManifestName), JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
        using (FileStream file = File.Open(nextState, FileMode.CreateNew))
        {
            JsonSerializer.Serialize(file, new AssetState(installation, previous), JsonOptions);
            file.Flush(flushToDisk: true);
        }

        string destination = GetDirectory(installation);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        token.ThrowIfCancellationRequested();
        Directory.Move(payload, destination);
        try
        {
            File.Move(nextState, StatePath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            TryDeleteDirectory(destination);
            throw;
        }

        foreach (AssetInstallation? retired in new[] { state.Active, state.Previous }.Distinct())
        {
            if (retired is { Id: var id } && id != Guid.Empty && retired != previous &&
                AssetReleaseManifest.IsReleaseVersion(retired.Version))
            {
                string directory = GetDirectory(retired);
                if (TryRead<AssetReleaseManifest>(Path.Combine(directory, ManifestName)) is { HasSplitResources: true })
                {
                    TryDeleteDirectory(Path.Combine(directory, "cache"));
                }
            }
        }
        return installation;
    }

    internal static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string GetDirectory(AssetInstallation installation) =>
        Path.Combine(RootDirectory, installation.Version.ToString(3), installation.Id.ToString("N"));

    private static AssetState ReadState() => TryRead<AssetState>(StatePath) ?? new AssetState(null, null);

    private static T? TryRead<T>(string path) where T : class
    {
        try
        {
            using FileStream file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(file, JsonOptions);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private sealed record AssetState(AssetInstallation? Active, AssetInstallation? Previous);
}
