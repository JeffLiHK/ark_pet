using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    public bool PhysicsModeEnabled { get; init; }
    public bool HardwareAccelerationEnabled { get; init; } = true;
    public int WalkingSpeedPercent { get; init; } = DefaultWalkingSpeedPercent;
    public int OutlineLevel { get; init; }
    public int ShadowLevel { get; init; }
    public int VoiceVolumePercent { get; init; } = 100;
    public string VoiceLanguage { get; init; } = Japanese;
    public string SubtitleLanguage { get; init; } = TraditionalChinese;

    public VoiceFavorite[] VoiceFavorites { get; init; } = [];
    public bool PreferFavoriteVoices { get; init; }

    public bool RememberDesktopLayout { get; init; }
    public SavedPet[] DesktopLayout { get; init; } = [];
    public DesktopConfiguration[] DesktopConfigurations { get; init; } = [];
    public bool DailyRotationEnabled { get; init; }
    public OutfitKey[] RotationCandidates { get; init; } = [];
    public DailyOutfit? DailyOutfit { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? OtherSettings { get; init; }
}

internal sealed record VoiceFavorite(string CharacterId, string LineId, string? Text);

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
                    VoiceFavorites = settings.VoiceFavorites is { } favorites
                        ? Array.FindAll(favorites, favorite => favorite is { CharacterId.Length: > 0, LineId.Length: > 0 })
                        : [],
                    DesktopLayout = settings.DesktopLayout is { } layout
                        ? Array.FindAll(layout, IsSavedPet)
                        : [],
                    DesktopConfigurations = (settings.DesktopConfigurations ?? [])
                        .Where(configuration => configuration is { Pets: { Length: > 0 } } &&
                            !string.IsNullOrWhiteSpace(configuration.Name) &&
                            configuration.Name.Trim().Length <= DesktopConfiguration.MaxNameLength &&
                            configuration.Pets.All(IsSavedPet) && configuration.Pets.Any(pet => pet.Slot == 0) &&
                            configuration.Pets.DistinctBy(pet => pet.Slot).Count() == configuration.Pets.Length)
                        .Select(configuration => configuration with { Name = configuration.Name.Trim() })
                        .DistinctBy(configuration => configuration.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
                    RotationCandidates = settings.RotationCandidates is { } candidates
                        ? Array.FindAll(candidates, outfit => outfit is { CharacterId.Length: > 0, OutfitId.Length: > 0 })
                        : [],
                    DailyOutfit = settings.DailyOutfit is { Outfit.CharacterId.Length: > 0, Outfit.OutfitId.Length: > 0 }
                        ? settings.DailyOutfit : null,
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

    private static bool IsSavedPet(SavedPet pet) =>
        pet is { Slot: >= 0, Outfit.CharacterId.Length: > 0, Outfit.OutfitId.Length: > 0 };

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
