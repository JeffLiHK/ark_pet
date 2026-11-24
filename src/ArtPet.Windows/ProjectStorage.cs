using System;
using System.IO;

namespace ArtPet.Windows;

internal static class ProjectStorage
{
    internal static string DataDirectory { get; } = Path.Combine(FindRoot(), "local-data");

    private static string FindRoot()
    {
        // A source build and a package inside this checkout share its data folder.
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ArtPet.slnx")))
            {
                return directory.FullName;
            }
        }

        return AppContext.BaseDirectory;
    }
}
