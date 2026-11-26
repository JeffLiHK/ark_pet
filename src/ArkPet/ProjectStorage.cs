using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ArtPet.Windows;

internal static class ProjectStorage
{
    private static readonly string? PublishedInstallDirectory = FindPublishedInstallDirectory();

    internal static string InstallDirectory { get; } = PublishedInstallDirectory ?? AppContext.BaseDirectory;
    internal static string DataDirectory { get; } = Path.Combine(FindRoot(), "local-data");
    private static string ResourceDirectory { get; } = Path.Combine(InstallDirectory, "resource");
    private static string AssetDataDirectory { get; } = PublishedInstallDirectory is null ? DataDirectory : ResourceDirectory;
    internal static string BundledAssetsDirectory { get; } = Path.Combine(ResourceDirectory, "assets");
    internal static string LegacyBundledAssetsDirectory { get; } = PublishedInstallDirectory is null
        ? Path.Combine(InstallDirectory, "assets") : Path.Combine(ResourceDirectory, "legacy-assets");
    internal static string ImportedAssetsDirectory { get; } = Path.Combine(AssetDataDirectory,
        PublishedInstallDirectory is null ? "assets" : "imported-assets");
    internal static string ImportedVoicesDirectory { get; } = Path.Combine(AssetDataDirectory, "voices");
    internal static string ManagedAssetsDirectory { get; } = Path.Combine(AssetDataDirectory, "managed-assets");

    private static string? FindPublishedInstallDirectory()
    {
        if (Environment.ProcessPath is { } process && Path.GetDirectoryName(process) is { } directory &&
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory).Equals(
                Path.Combine(directory, "data"), StringComparison.OrdinalIgnoreCase))
        {
            return directory;
        }

        return null;
    }

    internal static void MigrateInstalledResources()
    {
        if (PublishedInstallDirectory is null)
        {
            return;
        }

        // Only move files belonging to this installation. Packages inside the checkout used to share its local-data.
        string legacyData = Path.Combine(InstallDirectory, "local-data");
        MoveLegacyDirectory(Path.Combine(legacyData, "managed-assets"), ManagedAssetsDirectory);
        MoveLegacyDirectory(Path.Combine(legacyData, "assets"), ImportedAssetsDirectory);
        MoveLegacyDirectory(Path.Combine(legacyData, "voices"), ImportedVoicesDirectory);
        MoveLegacyDirectory(Path.Combine(InstallDirectory, "assets"), LegacyBundledAssetsDirectory);
    }

    private static void MoveLegacyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(ResourceDirectory);
            if (Directory.Exists(destination))
            {
                if (Directory.EnumerateFileSystemEntries(destination).Any())
                {
                    return;
                }

                Directory.Delete(destination);
            }
            Directory.Move(source, destination);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Could not migrate ArkPet resources from {0} to {1}: {2}", source, destination, error.Message);
        }
    }

    private static string FindRoot()
    {
        // A source build and a package inside this checkout share its data folder.
        for (DirectoryInfo? directory = new(InstallDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ArtPet.slnx")))
            {
                return directory.FullName;
            }
        }

        return InstallDirectory;
    }
}
