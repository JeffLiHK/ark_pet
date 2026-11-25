using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ArtPet.Windows.Wardrobe;

internal static class WardrobeProfessions
{
    internal static readonly ProfessionOption[] Options =
    [
        new("", "全部"),
        new("pioneer", "先鋒"),
        new("warrior", "近衛"),
        new("tank", "重裝"),
        new("sniper", "狙擊"),
        new("caster", "術師"),
        new("medic", "醫療"),
        new("support", "輔助"),
        new("special", "特種")
    ];

    private static readonly Dictionary<string, string> ByCharacter = ReadProfessions();

    internal static string? Find(string characterId)
    {
        string key = characterId.ToLowerInvariant();
        if (key.StartsWith("char_", StringComparison.Ordinal))
        {
            key = key[5..];
        }
        return ByCharacter.GetValueOrDefault(key.Replace('_', '-'));
    }

    private static Dictionary<string, string> ReadProfessions()
    {
        // ArknightsGameResource character_table.json at f1c69646e3578c7b53230e3b76c8ced121e0bfaf.
        using Stream source = typeof(WardrobeProfessions).Assembly
            .GetManifestResourceStream("ArtPet.CharacterProfessions.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(source)!;
    }

    internal sealed record ProfessionOption(string Id, string Name)
    {
        // In-game icons from MooncellWiki/prts-design at 38b02e40eee09d92e2314e234020f825eda75e2b.
        public Uri? Icon => Id.Length == 0 ? null : new Uri(
            $"pack://application:,,,/ArtPet.Windows;component/Wardrobe/ProfessionIcons/{Id}.png");
    }
}
