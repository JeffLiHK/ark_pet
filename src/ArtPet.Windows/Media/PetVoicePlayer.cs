using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace ArtPet.Windows.Media;

internal sealed class PetVoicePlayer(Action<string?> showSubtitle)
{
    private VoiceLine[] _lines = [];
    private MediaPlayer? _player;

    internal void SetCharacter(string characterId)
    {
        Stop();
        _lines = characterId == "hoolheyak"
            ? ReadLines(Path.Combine(ProjectStorage.DataDirectory, "voices", "霍爾海雅", "ja"))
            : [];
    }

    internal void PlayRandom()
    {
        Stop();
        VoiceLine[] available = _lines.Where(line => File.Exists(line.Path)).ToArray();
        if (available.Length == 0)
        {
            return;
        }

        VoiceLine line = available[Random.Shared.Next(available.Length)];
        var player = new MediaPlayer();
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

    private static VoiceLine[] ReadLines(string directory)
    {
        try
        {
            Dictionary<string, string?>? subtitles = JsonSerializer.Deserialize<Dictionary<string, string?>>(
                File.ReadAllText(Path.Combine(directory, "subtitles.json")));
            return subtitles?
                .Where(entry => Regex.IsMatch(entry.Key, @"\Acn_[0-9]{3}\z") && !string.IsNullOrWhiteSpace(entry.Value))
                .Select(entry => new VoiceLine(Path.Combine(directory, entry.Key + ".mp3"), entry.Value!))
                .ToArray() ?? [];
        }
        catch (Exception error) when (CharacterPack.IsAssetError(error) || error is JsonException)
        {
            return [];
        }
    }

    private sealed record VoiceLine(string Path, string Subtitle);
}
