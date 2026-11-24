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
    internal const int SupportedSchemaVersion = 1;
    private const string CharacterFolder = "霍爾海雅";
    private const string LegacyCharacterFolder = "hoolheyak";
    // Keep a 250-pixel canvas for the longest bundled outfit at 125% scale on a 125% display.
    private const long OutfitDecodedBytesBudget = 300L * 1024 * 1024;
    private const int MaximumOutfitFrames = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory;
    private readonly CanvasDefinition _canvas;

    private CharacterPack(string directory, CharacterDefinition definition, AssetInstallation? installation)
    {
        _directory = directory;
        _canvas = definition.Canvas;
        Id = definition.Id;
        DisplayName = definition.DisplayName;
        Outfits = definition.Outfits;
        DefaultOutfit = Outfits.Single(outfit => outfit.Id == definition.DefaultOutfitId);
        Installation = installation;
    }

    internal string Id { get; }
    internal string DisplayName { get; }
    internal IReadOnlyList<OutfitDefinition> Outfits { get; }
    internal OutfitDefinition DefaultOutfit { get; }
    internal AssetInstallation? Installation { get; }
    internal IEnumerable<OutfitDefinition> StartupOutfits => Outfits.Where(outfit => outfit != DefaultOutfit).Prepend(DefaultOutfit);

    internal static IEnumerable<CharacterPack> FindInstalled(Version appVersion)
    {
        foreach (CharacterPack pack in ManagedCharacterAssets.FindInstalled(appVersion))
        {
            yield return pack;
        }

        string bundledAssets = Path.Combine(AppContext.BaseDirectory, "assets");
        string localAssets = Path.Combine(ProjectStorage.DataDirectory, "assets");
        string legacyLocalCharacter = Path.Combine(localAssets, LegacyCharacterFolder);
        string[] directories =
        [
            Path.Combine(bundledAssets, CharacterFolder),
            Path.Combine(bundledAssets, LegacyCharacterFolder),
            Path.Combine(localAssets, CharacterFolder),
            Path.Combine(legacyLocalCharacter, "desktop-60"),
            Path.Combine(legacyLocalCharacter, "desktop")
        ];

        var candidates = directories
            .Concat(FindCharacterDirectories(bundledAssets))
            .Concat(FindCharacterDirectories(localAssets))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in candidates)
        {
            if (TryOpen(directory) is { } pack)
            {
                yield return pack;
            }
        }
    }

    private static IEnumerable<string> FindCharacterDirectories(string assets)
    {
        try
        {
            return Directory.GetDirectories(assets).Order(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    internal static CharacterPack? TryOpen(string directory, AssetInstallation? installation = null)
    {
        try
        {
            var definition = JsonSerializer.Deserialize<CharacterDefinition>(
                File.ReadAllText(Path.Combine(directory, "character.json")), JsonOptions);
            if (definition is not { SchemaVersion: SupportedSchemaVersion, Canvas: { } canvas } ||
                string.IsNullOrWhiteSpace(definition.Id) ||
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

            return new CharacterPack(directory, definition, installation);
        }
        catch (Exception error) when (IsAssetError(error) || error is JsonException)
        {
            return null;
        }
    }

    internal void ValidateForInstall(CancellationToken token)
    {
        var checkedClips = new HashSet<ClipDefinition>();
        foreach (OutfitDefinition outfit in Outfits)
        {
            if (outfit.Idle is null)
            {
                throw new InvalidDataException("造型缺少待機動畫。");
            }

            int remainingFrames = MaximumOutfitFrames;
            ClipDefinition?[] clips = [outfit.Idle, outfit.Touch, outfit.Move, outfit.Sit, outfit.Sleep];
            foreach (ClipDefinition clip in clips.OfType<ClipDefinition>())
            {
                if (!IsValidClip(clip, remainingFrames))
                {
                    throw new InvalidDataException("造型的動畫清單無效。");
                }

                remainingFrames -= clip.FrameCount;
                if (checkedClips.Add(clip))
                {
                    for (int index = 0; index < clip.FrameCount; index++)
                    {
                        token.ThrowIfCancellationRequested();
                        DecodeFrame(clip, index, _canvas.Width);
                    }
                }
            }

            foreach (string image in new[] { outfit.Preview, outfit.Portrait }.OfType<string>())
            {
                token.ThrowIfCancellationRequested();
                DecodeThumbnail(Resolve(image), 360, 720);
            }
        }
    }

    internal OutfitDefinition? FindLoadableOutfit(CancellationToken token)
    {
        foreach (OutfitDefinition outfit in StartupOutfits)
        {
            token.ThrowIfCancellationRequested();
            if (outfit.Idle is not { } idle || !IsValidClip(idle))
            {
                continue;
            }

            try
            {
                // Only idle is required. Validate every frame without retaining a full animation.
                int decodeWidth = GetDecodeWidth(idle.FrameCount);
                for (int index = 0; index < idle.FrameCount; index++)
                {
                    token.ThrowIfCancellationRequested();
                    DecodeFrame(idle, index, decodeWidth);
                }

                return outfit;
            }
            catch (Exception error) when (IsAssetError(error))
            {
            }
        }

        return null;
    }

    internal LoadedOutfit LoadOutfit(OutfitDefinition outfit, CancellationToken cancellationToken)
    {
        if (!IsValidClip(outfit.Idle))
        {
            throw new InvalidDataException("The outfit needs a valid idle animation.");
        }

        ClipDefinition idleDefinition = outfit.Idle!;
        ClipDefinition? touchDefinition = IsValidClip(outfit.Touch, MaximumOutfitFrames - idleDefinition.FrameCount) ? outfit.Touch : null;
        int frameCount = idleDefinition.FrameCount + (touchDefinition?.FrameCount ?? 0);
        ClipDefinition?[] candidates = [outfit.Move, outfit.Sit, outfit.Sleep];
        var variations = new List<ClipDefinition>(candidates.Length);
        foreach (ClipDefinition? candidate in candidates)
        {
            if (candidate is not null && IsValidClip(candidate, MaximumOutfitFrames - frameCount))
            {
                variations.Add(candidate);
                frameCount += candidate.FrameCount;
            }
        }

        int decodeWidth = GetDecodeWidth(frameCount);

        var idleClips = new List<FrameClip>(variations.Count + 1) { DecodeClip(idleDefinition, decodeWidth, cancellationToken) };
        FrameClip? move = null;
        foreach (ClipDefinition variation in variations)
        {
            if (DecodeOptionalClip(variation, decodeWidth, cancellationToken) is { } loaded)
            {
                idleClips.Add(loaded);
                if (ReferenceEquals(variation, outfit.Move))
                {
                    move = loaded;
                }
            }
        }

        FrameClip? touch = touchDefinition is null ? null : DecodeOptionalClip(touchDefinition, decodeWidth, cancellationToken);
        var anchor = new System.Windows.Point((double)_canvas.AnchorX / _canvas.Width, (double)_canvas.AnchorY / _canvas.Height);
        return new LoadedOutfit(outfit, idleClips, touch, anchor, LoadPreview(outfit), move);
    }

    internal BitmapSource? LoadPortrait(OutfitDefinition outfit)
    {
        try
        {
            string? portrait = outfit.Portrait;
            if (string.IsNullOrWhiteSpace(portrait))
            {
                if (outfit.Idle is not { Directory: { Length: > 0 } idleDirectory })
                {
                    return null;
                }

                portrait = Path.Combine(Path.GetDirectoryName(idleDirectory) ?? string.Empty, "portrait.png");
            }

            return DecodeThumbnail(Resolve(portrait), 360, 720);
        }
        catch (Exception error) when (IsAssetError(error))
        {
            return null;
        }
    }

    internal BitmapSource? LoadPreview(OutfitDefinition outfit)
    {
        if (!string.IsNullOrWhiteSpace(outfit.Preview))
        {
            try
            {
                return DecodeThumbnail(Resolve(outfit.Preview), 256, 256);
            }
            catch (Exception error) when (IsAssetError(error))
            {
                // The idle pose is available in packs without a separate preview.
            }
        }

        try
        {
            return outfit.Idle is { } idle ? DecodeThumbnail(ResolveFrame(idle, 0), 256, 256) : null;
        }
        catch (Exception error) when (IsAssetError(error))
        {
            return null;
        }
    }

    internal static bool IsAssetError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or
        FormatException or NotSupportedException or ArgumentException or InvalidOperationException;

    private static bool IsValidClip(ClipDefinition? clip, int maximumFrames = MaximumOutfitFrames) =>
        clip is { Fps: >= 1 and <= 60, FrameCount: > 0 } && clip.FrameCount <= maximumFrames &&
        !string.IsNullOrWhiteSpace(clip.Directory);

    private FrameClip? DecodeOptionalClip(ClipDefinition clip, int decodeWidth, CancellationToken cancellationToken)
    {
        try
        {
            return DecodeClip(clip, decodeWidth, cancellationToken);
        }
        catch (Exception error) when (IsAssetError(error))
        {
            return null;
        }
    }

    private int GetDecodeWidth(int frameCount)
    {
        double decodedBytes = 4d * _canvas.Width * _canvas.Height * frameCount;
        double scale = Math.Min(1, Math.Sqrt(OutfitDecodedBytesBudget / decodedBytes));
        return Math.Max(1, (int)Math.Floor(_canvas.Width * scale));
    }

    private FrameClip DecodeClip(ClipDefinition clip, int decodeWidth, CancellationToken cancellationToken)
    {
        var frames = new BitmapSource[clip.FrameCount];
        for (int index = 0; index < frames.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            frames[index] = DecodeFrame(clip, index, decodeWidth);
        }

        return new FrameClip(clip.Fps, frames);
    }

    private BitmapSource DecodeFrame(ClipDefinition clip, int index, int decodeWidth)
    {
        using var stream = File.OpenRead(ResolveFrame(clip, index));
        BitmapCacheOption cache = decodeWidth == _canvas.Width ? BitmapCacheOption.OnLoad : BitmapCacheOption.None;
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, cache);
        BitmapFrame frame = decoder.Frames[0];
        if (frame.PixelWidth != _canvas.Width || frame.PixelHeight != _canvas.Height)
        {
            throw new InvalidDataException("The animation frame size differs from the character canvas.");
        }

        if (decodeWidth == _canvas.Width)
        {
            frame.Freeze();
            return frame;
        }

        stream.Position = 0;
        return DecodeBitmap(stream, decodeWidth);
    }

    private static BitmapSource DecodeThumbnail(string file, int maximumWidth, int maximumHeight)
    {
        using var stream = File.OpenRead(file);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
        BitmapFrame frame = decoder.Frames[0];
        double scale = Math.Min(1, Math.Min((double)maximumWidth / frame.PixelWidth, (double)maximumHeight / frame.PixelHeight));
        stream.Position = 0;
        return DecodeBitmap(stream, Math.Max(1, (int)Math.Floor(frame.PixelWidth * scale)));
    }

    private static BitmapSource DecodeBitmap(Stream stream, int width)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = stream;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = width;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private string ResolveFrame(ClipDefinition clip, int index) =>
        Path.Combine(Resolve(clip.Directory), index.ToString("D4", CultureInfo.InvariantCulture) + ".png");

    private string Resolve(string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':'))
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

internal sealed record OutfitDefinition(string Id, string DisplayName, string? Preview, ClipDefinition? Idle, ClipDefinition? Touch,
    ClipDefinition? Move, ClipDefinition? Sit, ClipDefinition? Sleep, string? Portrait = null, bool MoveFacesLeft = false);

internal sealed record ClipDefinition(string Directory, int Fps, int FrameCount);

internal sealed record FrameClip(int Fps, BitmapSource[] Frames);

internal sealed record LoadedOutfit(OutfitDefinition Definition, IReadOnlyList<FrameClip> IdleClips, FrameClip? Touch,
    System.Windows.Point Anchor, BitmapSource? Preview, FrameClip? Move = null)
{
    internal FrameClip Idle => IdleClips[0];
}
