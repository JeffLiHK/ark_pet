using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArtPet.Windows;

internal sealed record PetSettings
{
    internal const int DefaultSize = 100;
    internal const int DefaultAnimationFps = 30;
    internal const int DefaultWalkingSpeedPercent = 100;
    internal const int DefaultIdleLoopsPerAction = 12;
    internal const int MinIdleLoopsPerAction = 1;
    internal const int MaxIdleLoopsPerAction = 100;
    internal const string Japanese = "ja";
    internal const string Mandarin = "zh-CN";
    internal const string TraditionalChinese = "zh-TW";
    internal const string SubtitlesOff = "off";
    internal static ReadOnlySpan<int> Sizes => [25, 50, 75, DefaultSize, 125, 150, 175, 200, 225, 250, 275, 300];
    internal static ReadOnlySpan<int> FrameRates => [DefaultAnimationFps, 60];
    internal static ReadOnlySpan<int> WalkingSpeeds => [50, 75, DefaultWalkingSpeedPercent, 125, 150];

    public int SchemaVersion { get; init; } = 1;
    public int SizePercent { get; init; } = DefaultSize;
    public int AnimationFps { get; init; } = DefaultAnimationFps;
    public int IdleLoopsPerAction { get; init; } = DefaultIdleLoopsPerAction;
    public bool InteractionEnabled { get; init; } = true;
    public bool FreeModeEnabled { get; init; }
    public int WalkingSpeedPercent { get; init; } = DefaultWalkingSpeedPercent;
    public int OutlineLevel { get; init; }
    public int ShadowLevel { get; init; }
    public int VoiceVolumePercent { get; init; } = 100;
    public string VoiceLanguage { get; init; } = Japanese;
    public string SubtitleLanguage { get; init; } = TraditionalChinese;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? OtherSettings { get; init; }
}

internal sealed class PetSettingsStore(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private bool _preserveOriginal;

    internal PetSettings Load()
    {
        try
        {
            using FileStream stream = File.OpenRead(filePath);
            PetSettings? settings = JsonSerializer.Deserialize<PetSettings>(stream, JsonOptions);
            if (settings is { SchemaVersion: 1 })
            {
                return settings with
                {
                    SizePercent = PetSettings.Sizes.Contains(settings.SizePercent) ? settings.SizePercent : PetSettings.DefaultSize,
                    AnimationFps = PetSettings.FrameRates.Contains(settings.AnimationFps) ? settings.AnimationFps : PetSettings.DefaultAnimationFps,
                    WalkingSpeedPercent = PetSettings.WalkingSpeeds.Contains(settings.WalkingSpeedPercent)
                        ? settings.WalkingSpeedPercent
                        : PetSettings.DefaultWalkingSpeedPercent,
                    OutlineLevel = Math.Clamp(settings.OutlineLevel, 0, 3),
                    ShadowLevel = Math.Clamp(settings.ShadowLevel, 0, 3),
                    VoiceVolumePercent = Math.Clamp(settings.VoiceVolumePercent, 0, 100),
                    IdleLoopsPerAction = settings.IdleLoopsPerAction is >= PetSettings.MinIdleLoopsPerAction and <= PetSettings.MaxIdleLoopsPerAction
                        ? settings.IdleLoopsPerAction
                        : PetSettings.DefaultIdleLoopsPerAction,
                    VoiceLanguage = settings.VoiceLanguage is PetSettings.Japanese or PetSettings.Mandarin
                        ? settings.VoiceLanguage
                        : PetSettings.Japanese,
                    SubtitleLanguage = settings.SubtitleLanguage is PetSettings.TraditionalChinese or PetSettings.Japanese or PetSettings.SubtitlesOff
                        ? settings.SubtitleLanguage
                        : PetSettings.TraditionalChinese
                };
            }

            _preserveOriginal = true;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            _preserveOriginal = true;
        }

        return new PetSettings();
    }

    internal bool TrySave(PetSettings settings)
    {
        string temporaryPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            using (FileStream stream = File.Open(temporaryPath, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(stream, settings, JsonOptions);
            }

            if (File.Exists(filePath))
            {
                string? backup = _preserveOriginal ? $"{filePath}.{Guid.NewGuid():N}.bak" : null;
                File.Replace(temporaryPath, filePath, backup);
            }
            else
            {
                File.Move(temporaryPath, filePath);
            }

            _preserveOriginal = false;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
