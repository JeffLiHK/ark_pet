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
    internal const int DefaultIdleLoopsPerAction = 12;
    internal const int MinIdleLoopsPerAction = 1;
    internal const int MaxIdleLoopsPerAction = 100;
    internal static ReadOnlySpan<int> Sizes => [75, DefaultSize, 125];
    internal static ReadOnlySpan<int> FrameRates => [DefaultAnimationFps, 60];

    public int SchemaVersion { get; init; } = 1;
    public int SizePercent { get; init; } = DefaultSize;
    public int AnimationFps { get; init; } = DefaultAnimationFps;
    public int IdleLoopsPerAction { get; init; } = DefaultIdleLoopsPerAction;
    public bool InteractionEnabled { get; init; } = true;

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
                    IdleLoopsPerAction = settings.IdleLoopsPerAction is >= PetSettings.MinIdleLoopsPerAction and <= PetSettings.MaxIdleLoopsPerAction
                        ? settings.IdleLoopsPerAction
                        : PetSettings.DefaultIdleLoopsPerAction
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
