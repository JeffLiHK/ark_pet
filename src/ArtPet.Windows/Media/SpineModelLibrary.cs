using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArtPet.Windows.Media;

internal sealed class SpineModelLibrary(string source, string cache, SpineFrameCache.SourcePack definition)
{
    private static readonly SemaphoreSlim Gate = new(1);
    private readonly Dictionary<string, string> _modelsByOutfit =
        definition.Outfits.ToDictionary(outfit => outfit.Id, outfit => outfit.ModelId, StringComparer.Ordinal);

    internal IEnumerable<SpineModelChoice> Models => definition.Outfits.GroupBy(outfit => outfit.ModelId)
        .Select(group => new SpineModelChoice(this, group.Key,
            $"{definition.DisplayName} · {string.Join(" / ", group.Select(outfit => outfit.DisplayName))}"));

    internal bool IsOutfitBuilt(OutfitDefinition outfit) => IsBuilt(_modelsByOutfit[outfit.Id]);
    internal bool IsBuilt(string modelId) => File.Exists(Path.Combine(ModelDirectory(modelId), "character.json"));

    internal async Task BuildAsync(string modelId, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            await GetOrBuildAsync(modelId, token);
        }
        finally
        {
            Gate.Release();
        }
    }

    internal async Task<LoadedOutfit> LoadAsync(OutfitDefinition outfit, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            CharacterPack generated = await GetOrBuildAsync(_modelsByOutfit[outfit.Id], token);
            OutfitDefinition data = generated.Outfits.Single(candidate => candidate.Id == outfit.Id);
            return generated.LoadOutfit(data, token) with { Definition = outfit };
        }
        finally
        {
            Gate.Release();
        }
    }

    internal async Task DeleteAsync(string modelId, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            string directory = ModelDirectory(modelId);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    internal CharacterPack? TryOpen(OutfitDefinition outfit) =>
        CharacterPack.TryOpen(ModelDirectory(_modelsByOutfit[outfit.Id]));

    private async Task<CharacterPack> GetOrBuildAsync(string modelId, CancellationToken token)
    {
        string destination = ModelDirectory(modelId);
        if (CharacterPack.TryOpen(destination) is { } existing)
        {
            return existing;
        }

        string staging = Path.Combine(cache, $".stage-{Guid.NewGuid():N}");
        try
        {
            await SpineFrameCache.GenerateAsync(source, staging, definition.Id, token, modelId);
            var built = CharacterPack.TryOpen(staging) ?? throw new InvalidDataException("模型建置缺少動畫清單。");
            built.ValidateArtwork(token);
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
            Directory.Move(staging, destination);
            return CharacterPack.TryOpen(destination) ?? throw new InvalidDataException("無法載入已建置的模型。");
        }
        finally
        {
            ManagedCharacterAssets.TryDeleteDirectory(staging);
        }
    }

    private string ModelDirectory(string modelId) =>
        _modelsByOutfit.ContainsValue(modelId) ? Path.Combine(cache, modelId) :
            throw new ArgumentException("Unknown Spine model.", nameof(modelId));
}

internal sealed record SpineModelChoice(SpineModelLibrary Library, string Id, string DisplayName);
