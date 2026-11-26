using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Threading;

namespace ArtPet.Windows.Media;

internal sealed record ResourceStorageUsage(long? Models, long? Japanese, long? Mandarin)
{
    private static readonly EnumerationOptions Entries = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false
    };

    private static readonly EnumerationOptions Tree = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false,
        RecurseSubdirectories = true
    };

    internal static ResourceStorageUsage Read(CancellationToken token)
    {
        IEnumerable<string> artworkRoots = Directories(ManagedCharacterAssets.RootDirectory)
            .Where(path => Version.TryParse(Path.GetFileName(path), out Version? version) &&
                AssetReleaseManifest.IsReleaseVersion(version))
            .SelectMany(Directories)
            .Concat([ProjectStorage.BundledAssetsDirectory, ProjectStorage.LegacyBundledAssetsDirectory,
                ProjectStorage.ImportedAssetsDirectory])
            .Distinct(StringComparer.OrdinalIgnoreCase);

        long? models = Measure(() => artworkRoots.Sum(root =>
        {
            string voicePrefix = Path.Combine(root, VoiceAssets.DirectoryName) + Path.DirectorySeparatorChar;
            return SumFiles(root, token, file => !IsAudio(file) ||
                !file.FullName.StartsWith(voicePrefix, StringComparison.OrdinalIgnoreCase));
        }));
        long? Voice(string language) => Measure(() =>
            VoiceLibrary.GetStorageDirectories(language).Sum(root => SumFiles(root, token)) +
            artworkRoots.Select(root => Path.Combine(root, VoiceAssets.DirectoryName))
                .Append(ProjectStorage.ImportedVoicesDirectory)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(Directories)
                .Sum(character => SumFiles(Path.Combine(character, language), token, IsAudio)));

        return new(models, Voice(PetSettings.Japanese), Voice(PetSettings.Mandarin));
    }

    private static bool IsAudio(FileInfo file) => file.Extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Directories(string root) => Directory.Exists(root)
        ? Directory.EnumerateDirectories(root, "*", Entries)
        : [];

    private static long SumFiles(string root, CancellationToken token, Func<FileInfo, bool>? include = null)
    {
        token.ThrowIfCancellationRequested();
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var files = new FileSystemEnumerable<FileInfo>(root,
            (ref FileSystemEntry entry) => (FileInfo)entry.ToFileSystemInfo(), Tree)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory,
            ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                !entry.FileName.StartsWith(".stage-", StringComparison.OrdinalIgnoreCase)
        };
        long total = 0;
        foreach (FileInfo file in files)
        {
            token.ThrowIfCancellationRequested();
            if (include is null || include(file))
            {
                total += file.Length;
            }
        }
        return total;
    }

    private static long? Measure(Func<long> read)
    {
        try
        {
            return read();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
