using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media;

namespace ArtPet.Windows.Media;

internal sealed class PetVoicePlayer(Action<string?> showSubtitle)
{
    private Task<VoiceLine[]>? _lines;
    private MediaPlayer? _player;
    private long _playRequest;
    private double _volume = 1;

    internal void SetVolume(int percent)
    {
        _volume = percent / 100d;
        if (_player is { } player)
        {
            player.Volume = _volume;
        }
    }

    internal void Configure(string? characterId, string voiceLanguage, string subtitleLanguage)
    {
        Stop();
        _lines = characterId == "hoolheyak"
            ? Task.Run(() => ReadLines(voiceLanguage, subtitleLanguage))
            : null;
    }

    internal async Task PlayRandomAsync()
    {
        Stop();
        if (_lines is not { } lines)
        {
            return;
        }

        long request = _playRequest;
        VoiceLine[] available = await lines;
        if (_playRequest != request || available.Length == 0)
        {
            return;
        }

        VoiceLine line = available[Random.Shared.Next(available.Length)];
        var player = new MediaPlayer { Volume = _volume };
        _player = player;
        player.MediaOpened += (_, _) =>
        {
            if (_player != player)
            {
                return;
            }

            if (!player.HasAudio)
            {
                Stop();
                return;
            }

            player.Play();
            showSubtitle(line.Subtitle);
        };
        player.MediaEnded += (_, _) => Finish(player);
        player.MediaFailed += (_, _) => Finish(player);

        try
        {
            player.Open(new Uri(line.Path, UriKind.Absolute));
        }
        catch (Exception error) when (CharacterPack.IsAssetError(error) || error is COMException)
        {
            Finish(player);
        }
    }

    internal void Stop()
    {
        _playRequest++;
        if (_player is not { } player)
        {
            return;
        }

        _player = null;
        player.Close();
        showSubtitle(null);
    }

    private void Finish(MediaPlayer player)
    {
        if (_player == player)
        {
            Stop();
        }
    }

    private static VoiceLine[] ReadLines(string voiceLanguage, string subtitleLanguage)
    {
        string root = Path.Combine(ProjectStorage.DataDirectory, "voices", "霍爾海雅");
        try
        {
            string[] paths = Directory.GetFiles(Path.Combine(root, voiceLanguage), "*.mp3");
            if (paths.Length == 0)
            {
                return [];
            }

            var subtitles = subtitleLanguage == PetSettings.SubtitlesOff
                ? null
                : ReadSubtitles(Path.Combine(root, subtitleLanguage, "subtitles.json"));

            return paths.Select(path => (Path: path, Id: Path.GetFileNameWithoutExtension(path).ToLowerInvariant()))
                .Where(line => Regex.IsMatch(line.Id, @"\Acn_[0-9]{3}\z"))
                .Select(line => new VoiceLine(line.Path, subtitles?.GetValueOrDefault(line.Id)))
                .ToArray();
        }
        catch (Exception error) when (CharacterPack.IsAssetError(error))
        {
            return [];
        }
    }

    private static Dictionary<string, string?>? ReadSubtitles(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(path));
        }
        catch (Exception error) when (CharacterPack.IsAssetError(error) || error is JsonException)
        {
            return null;
        }
    }

    private sealed record VoiceLine(string Path, string? Subtitle);
}
