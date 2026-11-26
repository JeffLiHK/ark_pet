using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;

namespace ArtPet.Windows.Media;

internal sealed class PetVoicePlayer(Action<string?> showSubtitle, string? managedVoiceRoot)
{
    private static readonly TimeSpan AudioDrainTime = TimeSpan.FromMilliseconds(400);
    private (string Character, string Voice, string Subtitle)? _selection;
    private MediaPlayer? _player;
    private long _playRequest;
    private bool _loading;
    private double _volume = 1;
    internal bool IsPlaying => _loading || _player is not null;

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
        _selection = characterId is not null && SpineFrameCache.IsIdentifier(characterId)
            ? (characterId, voiceLanguage, subtitleLanguage)
            : null;
    }

    internal async Task PlayRandomAsync()
    {
        if (IsPlaying || _selection is not { } selection)
        {
            return;
        }

        long request = _playRequest;
        _loading = true;
        VoiceLine[] available;
        try
        {
            available = await Task.Run(() =>
                ReadLines(selection.Character, managedVoiceRoot, selection.Voice, selection.Subtitle));
        }
        finally
        {
            if (_playRequest == request)
            {
                _loading = false;
            }
        }

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
        player.MediaEnded += async (_, _) =>
        {
            // Give the audio endpoint time to render its final buffered samples before closing.
            await Task.Delay(AudioDrainTime);
            Finish(player);
        };
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
        _loading = false;
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

    private static VoiceLine[] ReadLines(string characterId, string? voiceRoot, string voiceLanguage, string subtitleLanguage)
    {
        string localRoot = ProjectStorage.ImportedVoicesDirectory;
        string? managed = voiceRoot is null ? null : Path.Combine(voiceRoot, characterId);
        string local = Path.Combine(localRoot, characterId);
        string? legacy = characterId == ManagedCharacterAssets.LegacyPackId ? Path.Combine(localRoot, "霍爾海雅") : null;
        string?[] roots = [managed, local, legacy];

        string? downloaded = VoiceLibrary.GetVoiceRoot(voiceLanguage);
        string?[] audioRoots = [downloaded is null ? null : Path.Combine(downloaded, characterId), managed, local, legacy];
        string[] paths = audioRoots.OfType<string>().Select(root => FindAudio(root, voiceLanguage))
            .FirstOrDefault(files => files.Length > 0) ?? [];
        if (paths.Length == 0)
        {
            return [];
        }

        var subtitles = subtitleLanguage == PetSettings.SubtitlesOff
            ? null
            : roots.OfType<string>()
                .Select(root => ReadSubtitles(Path.Combine(root, subtitleLanguage, VoiceAssets.SubtitleFileName)))
                .FirstOrDefault(lines => lines is { Count: > 0 });
        return paths.Select(path => new VoiceLine(path,
            subtitles?.GetValueOrDefault(Path.GetFileNameWithoutExtension(path).ToLowerInvariant()))).ToArray();
    }

    private static string[] FindAudio(string root, string language)
    {
        try
        {
            return Directory.EnumerateFiles(Path.Combine(root, language), "*.mp3")
                .Where(path => VoiceAssets.IsLineId(Path.GetFileNameWithoutExtension(path).ToLowerInvariant()))
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
            return VoiceAssets.ReadSubtitles(path);
        }
        catch (Exception error) when (CharacterPack.IsAssetError(error) || error is JsonException)
        {
            return null;
        }
    }

    private sealed record VoiceLine(string Path, string? Subtitle);
}
