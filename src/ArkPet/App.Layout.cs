using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using ArtPet.Windows.Media;
using ArtPet.Windows.Desktop;

namespace ArtPet.Windows;

public partial class App
{
    private readonly List<string> _layoutNotices = [];
    private DispatcherTimer? _desktopSave;
    private bool _desktopReady;

    internal IReadOnlyList<string> LayoutNotices => _layoutNotices;
    internal string RotationStatus { get; private set; } = string.Empty;

    private async Task RestoreDesktopAsync(PetEntry primary, CancellationToken token)
    {
        SavedPet[] saved = _settings.RememberDesktopLayout ? _settings.DesktopLayout : [];
        SavedPet? main = saved.FirstOrDefault(pet => pet.Slot == 0);
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        bool rotated = await TryDailyRotationAsync(primary.Session, today, token);
        if (!rotated && main is not null)
        {
            await RestoreOutfitAsync(primary, main, token);
        }
        if (primary.Session.Outfit is null && !token.IsCancellationRequested)
        {
            await LoadStartupOutfitAsync(primary.Session);
        }
        if (token.IsCancellationRequested)
        {
            return;
        }

        RestorePetPosition(primary, main);
        foreach (SavedPet entry in saved.Where(pet => pet.Slot > 0).DistinctBy(pet => pet.Slot).OrderBy(pet => pet.Slot))
        {
            if (token.IsCancellationRequested)
            {
                return;
            }
            PetEntry pet = RegisterPet(new PetSession(), entry.Slot);
            await RestoreOutfitAsync(pet, entry, token);
            if (!token.IsCancellationRequested)
            {
                RestorePetPosition(pet, entry);
            }
        }
    }

    private async Task<bool> TryDailyRotationAsync(PetSession session, DateOnly today, CancellationToken token)
    {
        if (!_settings.DailyRotationEnabled)
        {
            return false;
        }

        if (_settings.DailyOutfit is { } selected && selected.Date == today)
        {
            if (await TryLoadOutfitAsync(session, selected.Outfit, token))
            {
                RotationStatus = $"今日使用「{session.Outfit!.Definition.DisplayName}」。";
                return true;
            }
            // A missing same-day choice can be replaced by another installed candidate.
        }

        OutfitKey[] candidates = _settings.RotationCandidates.Distinct().ToArray();
        Random.Shared.Shuffle(candidates);
        foreach (OutfitKey candidate in candidates)
        {
            if (await TryLoadOutfitAsync(session, candidate, token))
            {
                _settings = _settings with { DailyOutfit = new DailyOutfit(today, candidate) };
                SaveSettings();
                RotationStatus = $"今日使用「{session.Outfit!.Definition.DisplayName}」。";
                return true;
            }
        }
        RotationStatus = "輪值候選目前沒有可用造型，請調整候選清單。";
        return false;
    }

    private async Task RestoreOutfitAsync(PetEntry pet, SavedPet saved, CancellationToken token)
    {
        if (await TryLoadOutfitAsync(pet.Session, saved.Outfit, token))
        {
            return;
        }

        foreach (CharacterPack preferred in FindPacks(saved.Outfit.CharacterId))
        {
            if (await TryLoadOutfitAsync(pet.Session, new OutfitKey(preferred.Id, preferred.DefaultOutfit.Id), token))
            {
                break;
            }
        }
        if (pet.Session.Outfit is null && !token.IsCancellationRequested)
        {
            foreach (OutfitKey fallback in _installedPacks.SelectMany(pack =>
                pack.StartupOutfits.Where(pack.IsOutfitReady).Select(outfit => new OutfitKey(pack.Id, outfit.Id))))
            {
                if (await TryLoadOutfitAsync(pet.Session, fallback, token))
                {
                    break;
                }
            }
        }
        if (pet.Session.Outfit is null && !token.IsCancellationRequested)
        {
            // Use the normal startup builder if there are installed sources with no decoded cache.
            await LoadStartupOutfitAsync(pet.Session);
            foreach (OutfitKey fallback in _installedPacks.SelectMany(pack =>
                pack.StartupOutfits.Select(outfit => new OutfitKey(pack.Id, outfit.Id))))
            {
                if (pet.Session.Outfit is not null || token.IsCancellationRequested ||
                    await TryLoadOutfitAsync(pet.Session, fallback, token))
                {
                    break;
                }
            }
        }

        _layoutNotices.Add(pet.Session.Outfit is { } replacement
            ? $"小人 {pet.Slot + 1} 的原造型已不可用，已換成「{pet.Session.Pack!.DisplayName} · {replacement.Definition.DisplayName}」。"
            : $"小人 {pet.Slot + 1} 沒有可用造型。");
    }

    private IEnumerable<CharacterPack> FindPacks(string id) =>
        _installedPacks.Where(pack => StringComparer.OrdinalIgnoreCase.Equals(pack.Id, id));

    private async Task<bool> TryLoadOutfitAsync(PetSession session, OutfitKey key, CancellationToken token)
    {
        foreach (CharacterPack pack in FindPacks(key.CharacterId))
        {
            if (token.IsCancellationRequested)
            {
                break;
            }
            if (pack.Outfits.FirstOrDefault(outfit => outfit.Id == key.OutfitId) is not { } outfit ||
                session.HasLoadError(pack, outfit))
            {
                continue;
            }

            LoadedOutfit? decoded = _pets.Select(pet => pet.Session)
                .FirstOrDefault(other => other.Pack == pack && other.Outfit?.Definition == outfit)?.Outfit;
            if (decoded is not null)
            {
                session.RestoreDecodedOutfit(pack, decoded);
                return true;
            }

            await session.SelectAsync(pack, outfit, _settings.HardwareAccelerationEnabled, token);
            if (session.Pack == pack && session.Outfit?.Definition == outfit)
            {
                return true;
            }
        }
        return false;
    }

    private static void RestorePetPosition(PetEntry pet, SavedPet? saved)
    {
        if (saved is not null)
        {
            pet.Window.Topmost = saved.Topmost;
            pet.Window.SetPetInteraction(saved.AllowPetInteraction);
            ActivityArea activity = saved.Activity ?? new ActivityArea();
            pet.Window.SetActivityArea(Enum.IsDefined(activity.Scope) && Enum.IsDefined(activity.Corner)
                ? activity : new ActivityArea());
            if (pet.Window.HasOutfit && saved.Position is { } position &&
                double.IsFinite(position.X) && double.IsFinite(position.Y))
            {
                pet.Window.RestoreDesktopPosition(position);
            }
        }
        pet.Window.SetVisibilityPreference(saved?.Visible ?? true);
    }

    internal void SetRememberDesktop(bool enabled)
    {
        _settings = _settings with { RememberDesktopLayout = enabled, DesktopLayout = enabled ? _settings.DesktopLayout : [] };
        if (!SavePendingDesktop())
        {
            SaveSettings();
        }
    }

    internal void SetRotationEnabled(bool enabled)
    {
        _settings = _settings with { DailyRotationEnabled = enabled };
        RotationStatus = enabled ? "輪值在下次啟動時套用。" : string.Empty;
        SaveSettings();
    }

    internal void SetRotationCandidate(OutfitKey key, bool included)
    {
        OutfitKey[] remaining = _settings.RotationCandidates.Where(candidate => candidate != key).ToArray();
        _settings = _settings with { RotationCandidates = included ? [.. remaining, key] : remaining };
        SaveSettings();
    }

    private void RecordManualRotation(PetEntry pet, OutfitKey key)
    {
        if (pet != _primary || !_settings.DailyRotationEnabled)
        {
            return;
        }
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        _settings = _settings with { DailyOutfit = new DailyOutfit(today, key) };
        RotationStatus = $"今日保留手動選擇「{pet.Session.Outfit!.Definition.DisplayName}」。";
        SaveSettings();
    }

    private void ScheduleDesktopSave()
    {
        if (!_desktopReady || _exiting || !_settings.RememberDesktopLayout)
        {
            return;
        }
        if (_desktopSave is null)
        {
            _desktopSave = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            _desktopSave.Tick += (_, _) => SavePendingDesktop();
        }
        _desktopSave.Start();
    }

    private bool SavePendingDesktop()
    {
        _desktopSave?.Stop();
        if (!_desktopReady || !_settings.RememberDesktopLayout || _exiting || _primary is null)
        {
            return false;
        }

        var layout = new List<SavedPet>();
        foreach (PetEntry pet in Pets)
        {
            SavedPet? previous = _settings.DesktopLayout.FirstOrDefault(saved => saved.Slot == pet.Slot);
            if (pet.Session is { Pack: { } pack, Outfit: { } outfit })
            {
                layout.Add(new SavedPet(pet.Slot, new OutfitKey(pack.Id, outfit.Definition.Id),
                    pet.Window.CaptureDesktopPosition() ?? previous?.Position,
                    pet.Window.Topmost, pet.Window.WantsVisible, pet.Window.Activity, pet.Window.AllowPetInteraction));
            }
            else if (previous is not null)
            {
                layout.Add(previous with
                {
                    Visible = pet.Window.WantsVisible,
                    Topmost = pet.Window.Topmost,
                    Activity = pet.Window.Activity,
                    AllowPetInteraction = pet.Window.AllowPetInteraction
                });
            }
        }
        if (layout.SequenceEqual(_settings.DesktopLayout))
        {
            return false;
        }
        _settings = _settings with { DesktopLayout = [.. layout] };
        SaveSettings();
        return true;
    }

    private void SaveSettings()
    {
        if (!_settingsStore.TrySave(_settings))
        {
            _tray?.ShowSettingsSaveError();
        }
    }
}
