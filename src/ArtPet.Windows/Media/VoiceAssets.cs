using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace ArtPet.Windows.Media;

internal static class VoiceAssets
{
    internal const string DirectoryName = "voices";
    internal const string SubtitleFileName = "subtitles.json";

    internal static bool IsLineId(string id) => Regex.IsMatch(id, @"\Acn_[0-9]{3}\z");
    internal static bool IsAudioLanguage(string language) => language is PetSettings.Japanese or PetSettings.Mandarin;

    internal static bool IsPackageFile(string path, string? audioLanguage = null, bool subtitlesOnly = false)
    {
        string[] parts = path.Split(Path.DirectorySeparatorChar);
        if (parts.Length != 4 || parts[0] != DirectoryName || !SpineFrameCache.IsIdentifier(parts[1]))
        {
            return false;
        }

        string language = parts[2];
        string file = parts[3];
        return file == SubtitleFileName
            ? audioLanguage is null && language is (PetSettings.Japanese or PetSettings.TraditionalChinese)
            : !subtitlesOnly && (audioLanguage is null || audioLanguage == language) && IsAudioLanguage(language) &&
                file.EndsWith(".mp3", StringComparison.Ordinal) && IsLineId(Path.GetFileNameWithoutExtension(file));
    }

    internal static void Validate(string root, ArtworkCatalog? catalog, CancellationToken token, string? audioLanguage = null,
        bool subtitlesOnly = false)
    {
        var characters = catalog?.Characters.Select(character => character.Id).ToHashSet(StringComparer.Ordinal);
        var audio = new HashSet<(string Character, string Language)>();
        var subtitles = new HashSet<(string Character, string Language)>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, DirectoryName), "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(root, file);
            if (!IsPackageFile(relative, audioLanguage, subtitlesOnly))
            {
                throw new InvalidDataException("語音資源包含無效的檔案路徑。");
            }

            string[] parts = relative.Split(Path.DirectorySeparatorChar);
            if (characters is not null && !characters.Contains(parts[1]))
            {
                throw new InvalidDataException("語音資源包含角色清單以外的角色。");
            }

            if (parts[3] == SubtitleFileName)
            {
                try
                {
                    Dictionary<string, string?>? lines = ReadSubtitles(file);
                    if (lines is not { Count: > 0 } ||
                        lines.Any(line => !IsLineId(line.Key) || string.IsNullOrWhiteSpace(line.Value)))
                    {
                        throw new InvalidDataException("語音字幕清單無效。");
                    }
                }
                catch (JsonException error)
                {
                    throw new InvalidDataException("無法讀取語音字幕清單。", error);
                }
                subtitles.Add((parts[1], parts[2]));
            }
            else
            {
                using FileStream stream = File.OpenRead(file);
                int first = stream.ReadByte();
                int second = stream.ReadByte();
                int third = stream.ReadByte();
                if (stream.Length < 10 || !(first == 'I' && second == 'D' && third == '3' ||
                    first == 0xff && (second & 0xe0) == 0xe0))
                {
                    throw new InvalidDataException("語音資源包含無效的 MP3 檔案。");
                }
                audio.Add((parts[1], parts[2]));
            }
        }

        const string hoolheyak = ManagedCharacterAssets.LegacyPackId;
        if (audioLanguage is { } language)
        {
            if (!audio.Contains((hoolheyak, language)))
            {
                throw new InvalidDataException("配音包缺少霍爾海雅的配音。");
            }
            return;
        }
        if ((!subtitlesOnly && (!audio.Contains((hoolheyak, PetSettings.Japanese)) || !audio.Contains((hoolheyak, PetSettings.Mandarin)))) ||
            !subtitles.Contains((hoolheyak, PetSettings.Japanese)) || !subtitles.Contains((hoolheyak, PetSettings.TraditionalChinese)))
        {
            throw new InvalidDataException("資源包缺少霍爾海雅的日語、普通話配音或字幕。");
        }
    }

    internal static Dictionary<string, string?>? ReadSubtitles(string path)
    {
        using FileStream file = File.OpenRead(path);
        if (file.Length > 1024 * 1024)
        {
            throw new InvalidDataException("語音字幕清單超出大小限制。");
        }
        using var reader = new StreamReader(file);
        return JsonSerializer.Deserialize<Dictionary<string, string?>>(reader.ReadToEnd());
    }
}
