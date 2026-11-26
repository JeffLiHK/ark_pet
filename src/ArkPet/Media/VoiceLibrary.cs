using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ArtPet.Windows.Media;

internal static class VoiceLibrary
{
    private const string RemovedSuffix = ".removed-";
    private static string Root => Path.Combine(ManagedCharacterAssets.RootDirectory, "voice-packs");

    internal static bool HasChoice(string language) => File.Exists(StatePath(language));

    internal static AssetInstallation? GetInstalled(string language)
    {
        try
        {
            using FileStream file = File.Open(StatePath(language), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            AssetInstallation? installed = JsonSerializer.Deserialize<AssetInstallation>(file, ManagedCharacterAssets.JsonOptions);
            return installed is { Id: var id } && id != Guid.Empty && AssetReleaseManifest.IsReleaseVersion(installed.Version) &&
                Directory.Exists(InstallationDirectory(language, installed)) ? installed : null;
        }
        catch (Exception error) when (CharacterPack.IsAssetError(error) || error is JsonException)
        {
            return null;
        }
    }

    internal static string? GetVoiceRoot(string language) => GetInstalled(language) is { } installed
        ? Path.Combine(InstallationDirectory(language, installed), VoiceAssets.DirectoryName) : null;

    internal static void Activate(string language, Version version, string payload)
    {
        AssetInstallation? previous = GetInstalled(language);
        var installed = new AssetInstallation(version, Guid.NewGuid());
        string destination = InstallationDirectory(language, installed);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Directory.Move(payload, destination);
        try
        {
            WriteState(language, installed);
        }
        catch
        {
            ManagedCharacterAssets.TryDeleteDirectory(destination);
            throw;
        }
        if (previous is not null)
        {
            ManagedCharacterAssets.TryDeleteDirectory(InstallationDirectory(language, previous));
        }
    }

    internal static void Remove(string language)
    {
        string directory = GetStorageDirectory(language);
        string? removed = null;
        if (Directory.Exists(directory))
        {
            removed = directory + RemovedSuffix + Guid.NewGuid().ToString("N");
            Directory.Move(directory, removed);
        }
        try
        {
            WriteState(language, null);
        }
        catch
        {
            if (removed is not null)
            {
                Directory.Move(removed, directory);
            }
            throw;
        }
        if (removed is not null)
        {
            Directory.Delete(removed, recursive: true);
        }
    }

    private static void WriteState(string language, AssetInstallation? installation)
    {
        string state = StatePath(language);
        Directory.CreateDirectory(Root);
        string staging = state + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(staging, JsonSerializer.SerializeToUtf8Bytes(installation, ManagedCharacterAssets.JsonOptions));
            File.Move(staging, state, overwrite: true);
        }
        finally
        {
            File.Delete(staging);
        }
    }

    private static string StatePath(string language) => Path.Combine(Root, SafeLanguage(language) + ".json");
    internal static IEnumerable<string> GetStorageDirectories(string language)
    {
        yield return GetStorageDirectory(language);
        if (Directory.Exists(Root))
        {
            // A failed deletion can leave a renamed folder after the package is unregistered.
            foreach (string directory in Directory.EnumerateDirectories(Root, SafeLanguage(language) + RemovedSuffix + "*"))
            {
                yield return directory;
            }
        }
    }

    private static string GetStorageDirectory(string language) => Path.Combine(Root, SafeLanguage(language));

    private static string InstallationDirectory(string language, AssetInstallation installed) =>
        Path.Combine(GetStorageDirectory(language), installed.Id.ToString("N"));

    private static string SafeLanguage(string language) => VoiceAssets.IsAudioLanguage(language)
        ? language : throw new ArgumentException("Unknown voice language.", nameof(language));
}
