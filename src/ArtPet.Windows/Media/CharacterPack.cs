using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Media.Imaging;

namespace ArtPet.Windows.Media;

internal sealed class CharacterPack
{
    private const long OutfitPixelBudget = 40L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory;
    private readonly CanvasDefinition _canvas;

    private CharacterPack(string directory, CharacterDefinition definition)
    {
        _directory = directory;
        _canvas = definition.Canvas;
        DisplayName = definition.DisplayName;
        Outfits = definition.Outfits;
        DefaultOutfit = Outfits.Single(outfit => outfit.Id == definition.DefaultOutfitId);
    }

    internal string DisplayName { get; }
    internal IReadOnlyList<OutfitDefinition> Outfits { get; }
    internal OutfitDefinition DefaultOutfit { get; }

    internal static IEnumerable<CharacterPack> FindInstalled()
    {
        string[] directories =
        [
            Path.Combine(AppContext.BaseDirectory, "assets", "hoolheyak"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArtPet", "assets", "hoolheyak", "desktop")
        ];

        foreach (string directory in directories)
        {
            if (TryOpen(directory) is { } pack)
            {
                yield return pack;
            }
        }
    }

    private static CharacterPack? TryOpen(string directory)
    {
        try
        {
            var definition = JsonSerializer.Deserialize<CharacterDefinition>(
                File.ReadAllText(Path.Combine(directory, "character.json")), JsonOptions);
            if (definition is not { SchemaVersion: 1, Id: "hoolheyak", Canvas: { } canvas } ||
                string.IsNullOrWhiteSpace(definition.DisplayName) ||
                canvas.Width is <= 0 or > 512 || canvas.Height is <= 0 or > 512 ||
                canvas.AnchorX < 0 || canvas.AnchorX >= canvas.Width || canvas.AnchorY < 0 || canvas.AnchorY >= canvas.Height ||
                definition.Outfits is not { Length: > 0 } outfits ||
                outfits.Any(outfit => outfit is null || string.IsNullOrWhiteSpace(outfit.Id) || string.IsNullOrWhiteSpace(outfit.DisplayName)) ||
                outfits.Select(outfit => outfit.Id).Distinct(StringComparer.Ordinal).Count() != outfits.Length ||
                !outfits.Any(outfit => outfit.Id == definition.DefaultOutfitId))
            {
                return null;
            }

            return new CharacterPack(directory, definition);
        }
        catch (Exception error) when (IsAssetError(error) || error is JsonException)
        {
            return null;
        }
    }

    internal LoadedOutfit LoadOutfit(OutfitDefinition outfit, CancellationToken cancellationToken)
    {
        long maximumFrames = OutfitPixelBudget / (4L * _canvas.Width * _canvas.Height);
        if (!IsValidClip(outfit.Idle, maximumFrames))
        {
            throw new InvalidDataException("The outfit needs a valid idle animation within the pixel budget.");
        }

        FrameClip idle = DecodeClip(outfit.Idle!, cancellationToken);
        FrameClip? touch = null;
        if (IsValidClip(outfit.Touch, maximumFrames - idle.Frames.Length))
        {
            try
            {
                touch = DecodeClip(outfit.Touch!, cancellationToken);
            }
            catch (Exception error) when (IsAssetError(error))
            {
                // A missing optional clip uses the visual's brief breeze effect.
            }
        }

        return new LoadedOutfit(outfit, idle, touch);
    }

    internal BitmapSource? LoadPreview(OutfitDefinition outfit)
    {
        if (!string.IsNullOrWhiteSpace(outfit.Preview))
        {
            try
            {
                return DecodePreview(Resolve(outfit.Preview));
            }
            catch (Exception error) when (IsAssetError(error))
            {
                // The idle pose is available in packs without a separate preview.
            }
        }

        try
        {
            return outfit.Idle is { } idle ? DecodePreview(ResolveFrame(idle, 0)) : null;
        }
        catch (Exception error) when (IsAssetError(error))
        {
            return null;
        }
    }

    internal static bool IsAssetError(Exception error) => error is IOException or UnauthorizedAccessException or
        FormatException or NotSupportedException or ArgumentException or InvalidOperationException;

    private static bool IsValidClip(ClipDefinition? clip, long maximumFrames) =>
        clip is { Fps: >= 1 and <= 24, FrameCount: > 0 } && clip.FrameCount <= maximumFrames &&
        !string.IsNullOrWhiteSpace(clip.Directory);

    private FrameClip DecodeClip(ClipDefinition clip, CancellationToken cancellationToken)
    {
        var frames = new BitmapSource[clip.FrameCount];
        for (int index = 0; index < frames.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(ResolveFrame(clip, index));
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapFrame frame = decoder.Frames[0];
            if (frame.PixelWidth != _canvas.Width || frame.PixelHeight != _canvas.Height)
            {
                throw new InvalidDataException("The animation frame size differs from the character canvas.");
            }

            frame.Freeze();
            frames[index] = frame;
        }

        return new FrameClip(clip.Fps, frames);
    }

    private static BitmapSource DecodePreview(string file)
    {
        using var stream = File.OpenRead(file);
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = stream;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 112;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private string ResolveFrame(ClipDefinition clip, int index) =>
        Path.Combine(Resolve(clip.Directory), index.ToString("D4", CultureInfo.InvariantCulture) + ".png");

    private string Resolve(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("An asset path must be relative to its character folder.");
        }

        string path = Path.GetFullPath(relativePath, _directory);
        if (!path.StartsWith(_directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The asset path leaves the character folder.");
        }

        return path;
    }

    private sealed record CharacterDefinition(int SchemaVersion, string Id, string DisplayName, string DefaultOutfitId,
        CanvasDefinition Canvas, OutfitDefinition[] Outfits);

    private sealed record CanvasDefinition(int Width, int Height, int AnchorX, int AnchorY);
}

internal sealed record OutfitDefinition(string Id, string DisplayName, string? Preview, ClipDefinition? Idle, ClipDefinition? Touch);

internal sealed record ClipDefinition(string Directory, int Fps, int FrameCount);

internal sealed record FrameClip(int Fps, BitmapSource[] Frames);

internal sealed record LoadedOutfit(OutfitDefinition Definition, FrameClip Idle, FrameClip? Touch);
