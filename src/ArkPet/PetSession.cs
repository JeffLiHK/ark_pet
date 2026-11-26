using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ArtPet.Windows.Media;

namespace ArtPet.Windows;

internal sealed class PetSession : IDisposable
{
    private readonly SemaphoreSlim _decodeGate = new(1);
    private readonly HashSet<(CharacterPack Pack, string OutfitId)> _failedOutfits = [];
    private CancellationTokenSource? _request;

    internal CharacterPack? Pack { get; private set; }
    internal LoadedOutfit? Outfit { get; private set; }
    internal (CharacterPack Pack, OutfitDefinition Outfit)? PendingSelection { get; private set; }
    internal event EventHandler? Changed;

    // Decoded frames are frozen. Each copy keeps its own selections and pending load.
    internal PetSession Fork() => new() { Pack = Pack, Outfit = Outfit };

    internal bool HasLoadError(CharacterPack pack, OutfitDefinition outfit) => _failedOutfits.Contains((pack, outfit.Id));

    internal async Task SelectAsync(CharacterPack pack, OutfitDefinition outfit, bool hardwareAcceleration,
        CancellationToken token = default)
    {
        if (PendingSelection == (pack, outfit))
        {
            return;
        }

        _request?.Cancel();
        if (Pack == pack && Outfit?.Definition == outfit && pack.IsOutfitReady(outfit))
        {
            _request = null;
            PendingSelection = null;
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        using var request = token.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(token)
            : new CancellationTokenSource();
        _request = request;
        PendingSelection = (pack, outfit);
        _failedOutfits.Remove((pack, outfit.Id));
        Changed?.Invoke(this, EventArgs.Empty);

        try
        {
            await _decodeGate.WaitAsync(request.Token);
            try
            {
                LoadedOutfit loaded = await Task.Run(() => pack.LoadOutfitAsync(outfit, hardwareAcceleration, request.Token),
                    request.Token);
                if (_request == request && !request.IsCancellationRequested)
                {
                    Pack = pack;
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
        catch (Exception error) when (error is OutOfMemoryException || CharacterPack.IsAssetError(error))
        {
            if (_request == request)
            {
                _failedOutfits.Add((pack, outfit.Id));
            }
        }
        finally
        {
            if (_request == request)
            {
                _request = null;
                PendingSelection = null;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public void Dispose()
    {
        _request?.Cancel();
        _request = null;
        PendingSelection = null;
    }
}
