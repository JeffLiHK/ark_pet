using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ArtPet.Windows.Media;

namespace ArtPet.Windows;

internal sealed class PetSession(CharacterPack pack) : IDisposable
{
    private readonly SemaphoreSlim _decodeGate = new(1);
    private readonly HashSet<string> _failedOutfits = new(StringComparer.Ordinal);
    private CancellationTokenSource? _request;

    internal CharacterPack Pack { get; } = pack;
    internal LoadedOutfit? Outfit { get; private set; }
    internal OutfitDefinition? PendingOutfit { get; private set; }
    internal event EventHandler? Changed;

    internal bool HasLoadError(OutfitDefinition outfit) => _failedOutfits.Contains(outfit.Id);

    internal async Task SelectAsync(OutfitDefinition outfit)
    {
        if (PendingOutfit == outfit)
        {
            return;
        }

        _request?.Cancel();
        if (Outfit?.Definition == outfit)
        {
            _request = null;
            PendingOutfit = null;
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        using var request = new CancellationTokenSource();
        _request = request;
        PendingOutfit = outfit;
        _failedOutfits.Remove(outfit.Id);
        Changed?.Invoke(this, EventArgs.Empty);

        try
        {
            await _decodeGate.WaitAsync(request.Token);
            try
            {
                LoadedOutfit loaded = await Task.Run(() => Pack.LoadOutfit(outfit, request.Token), request.Token);
                if (_request == request)
                {
                    Outfit = loaded;
                }
            }
            finally
            {
                _decodeGate.Release();
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception error) when (CharacterPack.IsAssetError(error))
        {
            if (_request == request)
            {
                _failedOutfits.Add(outfit.Id);
            }
        }
        finally
        {
            if (_request == request)
            {
                _request = null;
                PendingOutfit = null;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public void Dispose()
    {
        _request?.Cancel();
        _request = null;
        PendingOutfit = null;
    }
}
