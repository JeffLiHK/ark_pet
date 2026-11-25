using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Media.Imaging;

namespace ArtPet.Windows.Media;

internal sealed record ArtworkCatalog(ArtworkCharacter[] Characters)
{
    internal const int SchemaVersion = 3;
    internal const int VoiceSchemaVersion = 4;
    internal const string PackId = "artpet";
    internal const string CatalogName = "catalog.json";
    internal const string IconName = "app.ico";
    internal const string SproutId = "sprout";

    internal static ArtworkCatalog Read(string directory)
    {
        using FileStream file = File.OpenRead(Path.Combine(directory, CatalogName));
        if (file.Length > 64 * 1024 ||
            JsonSerializer.Deserialize<ArtworkCatalog>(file, ManagedCharacterAssets.JsonOptions) is not
                { Characters: { Length: > 0 and <= 64 } characters } catalog ||
            characters.Any(character => character is null || !SpineFrameCache.IsIdentifier(character.Id) ||
                character.SchemaVersion is not (CharacterPack.SupportedSchemaVersion or SpineFrameCache.SchemaVersion)) ||
            characters.Select(character => character.Id).Distinct(StringComparer.Ordinal).Count() != characters.Length ||
            !characters.Any(character => character.Id == SproutId) ||
            !characters.Any(character => character.Id == ManagedCharacterAssets.LegacyPackId))
        {
            throw new InvalidDataException("完整美術資源包的角色清單無效。");
        }

        return catalog;
    }

    internal static BitmapSource LoadAppIcon(string directory) =>
        CharacterPack.DecodeThumbnail(Path.Combine(directory, IconName), 256, 256);
}

internal sealed record ArtworkCharacter(string Id, int SchemaVersion)
{
    internal bool HasSpineSource => SchemaVersion == SpineFrameCache.SchemaVersion;

    internal string SourceDirectory(string directory) => Path.Combine(directory, "characters", Id);

    internal string PlaybackDirectory(string directory) =>
        HasSpineSource ? Path.Combine(directory, "cache", Id) : SourceDirectory(directory);

    internal CharacterPack LoadValidated(string directory, CancellationToken token)
    {
        if (CharacterPack.TryOpen(PlaybackDirectory(directory)) is not { } pack || pack.Id != Id)
        {
            throw new InvalidDataException($"角色 {Id} 缺少有效的美術資源。");
        }

        pack.ValidateArtwork(token);
        return pack;
    }
}
