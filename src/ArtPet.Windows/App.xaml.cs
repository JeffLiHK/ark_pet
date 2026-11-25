using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ArtPet.Windows.Desktop;
using ArtPet.Windows.Media;
using ArtPet.Windows.Wardrobe;

namespace ArtPet.Windows;

public partial class App : Application
{
    private Mutex? _instance;
    private TrayIcon? _tray;
    private readonly List<PetEntry> _pets = [];
    private readonly CancellationTokenSource _startup = new();
    private readonly PetSettingsStore _settingsStore = new(Path.Combine(ProjectStorage.DataDirectory, "settings.json"));
    private PetEntry? _primary;
    private PetSettings _settings = new();
    private CharacterPack[] _installedPacks = [];
    private AssetSnapshot? _artwork;
    private SettingsWindow? _settingsWindow;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = new Mutex(initiallyOwned: false, @"Local\ArtPet.Windows", out bool firstInstance);
        if (!firstInstance)
        {
            Shutdown();
            return;
        }

        _settings = _settingsStore.Load();
        CancellationToken token = _startup.Token;
        (_artwork, _installedPacks) = await Task.Run(() =>
        {
            ProjectStorage.MigrateInstalledResources();
            AssetSnapshot? artwork = ManagedCharacterAssets.LoadInstalled(ReleaseUpdater.CurrentVersion);
            return (artwork, CharacterPack.FindInstalled(artwork).ToArray());
        });
        if (_exiting)
        {
            return;
        }

        string? setupStatus = null;
        if (_artwork is null)
        {
            setupStatus = await InitializeArtworkAsync(token);
        }
        if (_exiting)
        {
            return;
        }

        var session = new PetSession();
        var primary = RegisterPet(session, 0);
        _primary = primary;
        MainWindow = primary.Window;
        await LoadStartupOutfitAsync(session);
        if (_exiting)
        {
            return;
        }

        _tray = new TrayIcon(primary.Window, ExitApplication, OpenSettings, WardrobeCommand(primary),
            () => AddPet(primary), GetTrayPets, _artwork?.AppIcon);
        primary.Window.ShowPet();
        if (setupStatus is not null)
        {
            _tray.ShowSetupStatus(setupStatus);
        }
        if (!primary.Window.HasOutfit && _installedPacks.Length > 0)
        {
            OpenWardrobe(primary);
        }
        await CheckStartupUpdateAsync(token);
    }

    private async Task LoadStartupOutfitAsync(PetSession session)
    {
        // The default outfit can build its frames on the first launch.
        var preferredOutfits = _installedPacks
            .Where(pack => StringComparer.OrdinalIgnoreCase.Equals(pack.Id, ManagedCharacterAssets.LegacyPackId))
            .Select(pack => (Pack: pack, Outfit: pack.DefaultOutfit));
        var readyOutfits = _installedPacks.SelectMany(pack => pack.StartupOutfits
            .Where(pack.IsOutfitReady)
            .Select(outfit => (Pack: pack, Outfit: outfit)));

        using var build = new CancellationTokenSource();
        ArtworkSetupWindow? progress = null;
        try
        {
            foreach (var (pack, outfit) in preferredOutfits.Concat(readyOutfits).Distinct())
            {
                if (pack.IsOutfitReady(outfit))
                {
                    await session.SelectAsync(pack, outfit);
                }
                else if (!build.IsCancellationRequested)
                {
                    if (progress is null)
                    {
                        progress = new ArtworkSetupWindow();
                        progress.ShowModelBuild($"正在建置{pack.DisplayName}的{outfit.DisplayName}…");
                        progress.Closed += (_, _) => build.Cancel();
                        progress.Show();
                    }
                    await session.SelectAsync(pack, outfit, build.Token);
                }

                if (_exiting || session.Outfit is not null)
                {
                    return;
                }
            }
        }
        finally
        {
            if (progress?.IsVisible == true)
            {
                progress.Close();
            }
        }
    }

    private async Task<string?> InitializeArtworkAsync(CancellationToken token)
    {
        using var setup = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationToken setupToken = setup.Token;
        var window = new ArtworkSetupWindow();
        window.Closed += (_, _) => setup.Cancel();
        window.Show();
        try
        {
            AssetReleaseCheck result = await AssetReleaseUpdater.CheckAsync(null, setupToken);
            if (result.Update is not { } update)
            {
                return result.Message;
            }

            var progress = new Progress<string>(window.ShowProgress);
            await Task.Run(() => AssetReleaseUpdater.InstallAsync(update, null, progress, setupToken,
                requireJapaneseVoice: true), setupToken);
            (_artwork, _installedPacks) = await Task.Run(() =>
            {
                AssetSnapshot? artwork = ManagedCharacterAssets.LoadInstalled(ReleaseUpdater.CurrentVersion);
                return (artwork, CharacterPack.FindInstalled(artwork).ToArray());
            }, token);
            AssetReleaseUpdater.ConfirmRunning(_artwork?.Installation);
            return _artwork is null ? "無法讀取已下載的資源。" : null;
        }
        catch (OperationCanceledException) when (setupToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException ||
            CharacterPack.IsAssetError(error))
        {
            return "未能完成資源初始化。可在設定的資源管理重試。";
        }
        finally
        {
            if (window.IsVisible)
            {
                window.Close();
            }
        }
    }

    private async Task CheckStartupUpdateAsync(CancellationToken token)
    {
        ReleaseCheck result;
        try
        {
            result = await ReleaseUpdater.CheckAsync(token);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException ||
            CharacterPack.IsAssetError(error))
        {
            return;
        }

        if (_exiting || !result.NewerVersionAvailable)
        {
            return;
        }

        if (result.Update is not { } update || !ReleaseUpdater.CanInstall)
        {
            _tray?.ShowSetupStatus(result.Message);
            return;
        }

        if (MessageBox.Show($"有新版 Art Pet {update.Version.ToString(3)}。要下載更新並重新啟動嗎？",
                "Art Pet", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) == MessageBoxResult.Yes)
        {
            OpenSettings();
            if (_settingsWindow is { } settings)
            {
                await settings.InstallStartupUpdateAsync(result);
            }
        }
    }

    private PetEntry RegisterPet(PetSession session, int slot)
    {
        var window = new PetWindow(slot, _artwork?.VoiceRoot) { Icon = _artwork?.AppIcon };
        var pet = new PetEntry(slot, window, session);
        _pets.Add(pet);
        window.ApplySettings(_settings);
        window.AddSettingsCommand(OpenSettings);
        window.AddPetCommands(() => AddPet(pet), slot == 0 ? null : () => RemovePet(pet));
        if (_installedPacks.Length > 0)
        {
            window.AddWardrobeCommand(() => OpenWardrobe(pet));
        }

        session.Changed += (_, _) => DisplayOutfit(pet);
        window.Closed += (_, _) =>
        {
            _pets.Remove(pet);
            session.Dispose();
            if (pet == _primary)
            {
                ExitApplication();
            }
        };
        DisplayOutfit(pet);
        return pet;
    }

    private void AddPet(PetEntry source)
    {
        if (source.Session.Outfit is null)
        {
            OpenWardrobe(source);
            return;
        }

        var occupied = _pets.Select(pet => pet.Slot).ToHashSet();
        int slot = 1;
        while (occupied.Contains(slot))
        {
            slot++;
        }

        RegisterPet(source.Session.Fork(), slot).Window.ShowPet();
    }

    private static void RemovePet(PetEntry pet)
    {
        pet.Wardrobe?.Close();
        pet.Window.Close();
    }

    private Action? WardrobeCommand(PetEntry pet) => _installedPacks.Length > 0 ? () => OpenWardrobe(pet) : null;

    private IEnumerable<TrayPet> GetTrayPets() => _pets.OrderBy(pet => pet.Slot).Select(pet =>
        new TrayPet($"小人 {pet.Slot + 1} · {pet.Session.Pack?.DisplayName ?? "尚未選擇角色"}", pet.Window,
            WardrobeCommand(pet), pet == _primary ? null : () => RemovePet(pet)));

    private void DisplayOutfit(PetEntry pet)
    {
        if (pet.Session is not { Pack: { } pack, Outfit: { } outfit } || pet.Displayed == outfit)
        {
            return;
        }

        pet.Displayed = outfit;
        bool firstOutfit = !pet.Window.HasOutfit;
        pet.Window.SetVoiceCharacter(pack.Id);
        pet.Window.SetOutfit(outfit);
        if (firstOutfit && _tray is not null)
        {
            pet.Window.ShowPet();
        }
        var icon = outfit.Icon ?? CharacterIcon.Create(outfit) ?? _artwork?.AppIcon;
        pet.Window.Icon = icon;
        if (pet == _primary)
        {
            _tray?.SetCharacterIcon(icon);
            if (_settingsWindow is { } settings)
            {
                settings.Icon = _artwork?.AppIcon ?? icon;
            }
        }

        if (pet.Wardrobe is { } wardrobe)
        {
            wardrobe.Icon = icon;
        }
    }

    private void OpenSettings()
    {
        if (_settingsWindow is { } open)
        {
            open.BringToFront();
            return;
        }

        var settingsWindow = new SettingsWindow(_settings, _artwork, () =>
        {
            foreach (PetEntry pet in _pets)
            {
                pet.Window.StopVoice();
            }
        })
        {
            Icon = _artwork?.AppIcon ?? _primary?.Window.Icon
        };
        _settingsWindow = settingsWindow;
        settingsWindow.SettingsSelected += settings =>
        {
            _settings = settings;
            foreach (PetEntry entry in _pets)
            {
                entry.Window.ApplySettings(settings);
            }

            if (!_settingsStore.TrySave(settings))
            {
                _tray?.ShowSettingsSaveError();
            }
        };
        settingsWindow.Closed += (_, _) => _settingsWindow = null;
        settingsWindow.Show();
        settingsWindow.BringToFront();
    }

    private void OpenWardrobe(PetEntry pet)
    {
        if (pet.Wardrobe is { } open)
        {
            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }

            open.Activate();
            return;
        }

        var wardrobe = new WardrobeWindow(pet.Session, _installedPacks) { Icon = pet.Window.Icon };
        pet.Wardrobe = wardrobe;
        wardrobe.Closed += (_, _) => pet.Wardrobe = null;
        wardrobe.ShowNear(pet.Window);
    }

    private void ExitApplication()
    {
        if (!_exiting)
        {
            _exiting = true;
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _startup.Cancel();
        _startup.Dispose();
        foreach (PetEntry pet in _pets)
        {
            pet.Session.Dispose();
        }

        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private sealed record PetEntry(int Slot, PetWindow Window, PetSession Session)
    {
        internal LoadedOutfit? Displayed { get; set; }
        internal WardrobeWindow? Wardrobe { get; set; }
    }
}
