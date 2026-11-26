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

    internal Task ManageOutfitAsync(OutfitDefinition outfit, bool remove, bool hardwareAcceleration, CancellationToken token) =>
        remove ? DeleteAsync(_modelsByOutfit[outfit.Id], token) :
            BuildAsync(_modelsByOutfit[outfit.Id], hardwareAcceleration, token);

    internal bool ShareModel(OutfitDefinition first, OutfitDefinition second) =>
        _modelsByOutfit[first.Id] == _modelsByOutfit[second.Id];

    internal bool IsOutfitBuilt(OutfitDefinition outfit) => IsBuilt(_modelsByOutfit[outfit.Id]);
    internal bool IsBuilt(string modelId) => File.Exists(Path.Combine(ModelDirectory(modelId), "character.json"));

    private async Task BuildAsync(string modelId, bool hardwareAcceleration, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            await GetOrBuildAsync(modelId, hardwareAcceleration, token);
        }
        finally
        {
            Gate.Release();
        }
    }

    internal async Task<LoadedOutfit> LoadAsync(OutfitDefinition outfit, bool hardwareAcceleration, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            var (generated, decoded) = await GetOrBuildAsync(_modelsByOutfit[outfit.Id], hardwareAcceleration, token, outfit.Id);
            decoded ??= generated.LoadOutfit(generated.Outfits.Single(candidate => candidate.Id == outfit.Id), token);
            return decoded with { Definition = outfit };
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task DeleteAsync(string modelId, CancellationToken token)
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

    private async Task<(CharacterPack Pack, LoadedOutfit? Decoded)> GetOrBuildAsync(string modelId,
        bool hardwareAcceleration, CancellationToken token, string? outfitId = null)
    {
        string destination = ModelDirectory(modelId);
        if (CharacterPack.TryOpen(destination) is { } existing)
        {
            return (existing, null);
        }

        string staging = Path.Combine(cache, $".stage-{Guid.NewGuid():N}");
        try
        {
            await SpineFrameCache.GenerateAsync(source, staging, definition.Id, hardwareAcceleration, token, modelId);
            var built = CharacterPack.TryOpen(staging) ?? throw new InvalidDataException("模型建置缺少動畫清單。");
            OutfitDefinition? selected = outfitId is null ? null : built.Outfits.Single(candidate => candidate.Id == outfitId);
            LoadedOutfit? decoded = selected is null ? null : built.LoadOutfit(selected, token, requireAllClips: true);
            built.ValidateArtwork(token, selected);
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
            Directory.Move(staging, destination);
            var installed = CharacterPack.TryOpen(destination) ?? throw new InvalidDataException("無法載入已建置的模型。");
            return (installed, decoded);
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
