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
    private MainInterfaceWindow? _mainInterface;
    private string? _assetStatus;
    private readonly List<VoiceFavorite> _recentVoices = [];
    internal DesktopScenario Scenario { get; private set; }
    internal PetSettings Settings => _settings;
    internal IReadOnlyList<CharacterPack> InstalledPacks => _installedPacks;
    internal IEnumerable<PetEntry> Pets => _pets.OrderBy(pet => pet.Slot);
    internal IReadOnlyList<VoiceFavorite> RecentVoices => _recentVoices;
    internal string? AssetStatus => _assetStatus;
    internal string? VoiceRoot => _artwork?.VoiceRoot;

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
        await RestoreDesktopAsync(primary, token);
        if (_exiting)
        {
            return;
        }

        _assetStatus = setupStatus;
        _tray = new TrayIcon(primary.Window, ExitApplication, OpenSettings, OpenMainInterface, WardrobeCommand(primary),
            () => AddPet(primary), GetTrayPets, _artwork?.AppIcon);
        _desktopReady = true;
        SavePendingDesktop();
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
                    await session.SelectAsync(pack, outfit, _settings.HardwareAccelerationEnabled);
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
                    await session.SelectAsync(pack, outfit, _settings.HardwareAccelerationEnabled, build.Token);
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
            bool hardwareAcceleration = _settings.HardwareAccelerationEnabled;
            await Task.Run(() => AssetReleaseUpdater.InstallAsync(update, null, progress, setupToken,
                hardwareAcceleration, requireJapaneseVoice: true), setupToken);
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

        if (MessageBox.Show($"有新版 ArkPet {update.Version.ToString(3)}。要下載更新並重新啟動嗎？",
                "ArkPet", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) == MessageBoxResult.Yes)
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
        window.SetScenario(Scenario);
        window.DesktopStateChanged += (_, _) =>
        {
            _mainInterface?.Refresh();
            ScheduleDesktopSave();
        };
        window.LocationChanged += (_, _) => ScheduleDesktopSave();
        window.VoicePlayed += RecordVoice;
        window.DragCompleted += (_, _) => TryPetInteraction(pet);
        window.AddSettingsCommand(OpenSettings);
        window.AddPetCommands(() => AddPet(pet), slot == 0 ? null : () => RemovePet(pet));
        if (_installedPacks.Length > 0)
        {
            window.AddWardrobeCommand(() => OpenWardrobe(pet));
        }

        session.Changed += (_, _) =>
        {
            DisplayOutfit(pet);
            _mainInterface?.Refresh();
        };
        window.Closed += (_, _) =>
        {
            _pets.Remove(pet);
            session.Dispose();
            _mainInterface?.Refresh();
            if (pet != _primary)
            {
                ScheduleDesktopSave();
            }
            if (pet == _primary)
            {
                ExitApplication();
            }
        };
        DisplayOutfit(pet);
        return pet;
    }

    internal PetEntry? AddPet(PetEntry source, bool select = false)
    {
        if (source.Session.Outfit is null)
        {
            OpenWardrobe(source);
            return null;
        }

        var occupied = _pets.Select(pet => pet.Slot).ToHashSet();
        int slot = 1;
        while (occupied.Contains(slot))
        {
            slot++;
        }

        PetEntry pet = RegisterPet(source.Session.Fork(), slot);
        pet.Window.ShowPet();
        if (select)
        {
            _mainInterface?.SelectPet(pet);
        }
        return pet;
    }

    internal static void RemovePet(PetEntry pet)
    {
        pet.Wardrobe?.Close();
        pet.Window.Close();
    }

    private void TryPetInteraction(PetEntry source)
    {
        if (!source.Window.CanInteractWithPet || source.Window.DesktopFeet is not { } feet)
        {
            return;
        }

        var nearest = _pets.Where(pet => pet != source && pet.Window.IsVisible)
            .Select(pet => (pet.Window, Feet: pet.Window.DesktopFeet))
            .Where(pet => pet.Feet.HasValue)
            .OrderBy(pet => (pet.Feet!.Value - feet).LengthSquared)
            .FirstOrDefault();
        if (nearest.Window is { CanInteractWithPet: true } other && nearest.Feet is { } otherFeet)
        {
            source.Window.InteractWithPet(otherFeet.X - feet.X);
            other.InteractWithPet(feet.X - otherFeet.X);
        }
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
        if (_desktopReady)
        {
            RecordManualRotation(pet, new OutfitKey(pack.Id, outfit.Definition.Id));
            ScheduleDesktopSave();
        }
        bool firstOutfit = !pet.Window.HasOutfit;
        pet.Window.SetVoiceCharacter(pack.Id);
        pet.Window.SetOutfit(outfit);
        if (firstOutfit && _tray is not null && pet.Window.WantsVisible)
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

    internal void OpenSettings() => ShowSettings();

    internal void OpenResources(bool download = false) => ShowSettings(resources: true, download);

    private void ShowSettings(bool resources = false, bool download = false)
    {
        if (_settingsWindow is { } open)
        {
            if (resources)
            {
                open.OpenResources(download);
            }
            open.BringToFront();
            return;
        }

        var settingsWindow = new SettingsWindow(_settings, _artwork, () =>
        {
            foreach (PetEntry pet in _pets)
            {
                pet.Window.StopVoice();
            }
            _mainInterface?.StopVoice();
        })
        {
            Icon = _artwork?.AppIcon ?? _primary?.Window.Icon
        };
        _settingsWindow = settingsWindow;
        settingsWindow.SettingsSelected += settings => UpdateSettings(settings with
        {
            VoiceFavorites = _settings.VoiceFavorites,
            PreferFavoriteVoices = _settings.PreferFavoriteVoices,
            RememberDesktopLayout = _settings.RememberDesktopLayout,
            DesktopLayout = _settings.DesktopLayout,
            DesktopConfigurations = _settings.DesktopConfigurations,
            DailyRotationEnabled = _settings.DailyRotationEnabled,
            RotationCandidates = _settings.RotationCandidates,
            DailyOutfit = _settings.DailyOutfit
        }, applyToPets: true);
        settingsWindow.VoiceAssetsChanged += () => _mainInterface?.RefreshVoiceAssets();
        settingsWindow.AssetStatusChanged += message =>
        {
            _assetStatus = message;
            _mainInterface?.Refresh();
        };
        settingsWindow.Closed += (_, _) => _settingsWindow = null;
        settingsWindow.Show();
        if (resources)
        {
            settingsWindow.OpenResources(download);
        }
        settingsWindow.BringToFront();
    }

    private void OpenMainInterface()
    {
        if (_mainInterface is null)
        {
            _mainInterface = new MainInterfaceWindow(this) { Icon = _artwork?.AppIcon ?? _primary?.Window.Icon };
            _mainInterface.Closed += (_, _) => _mainInterface = null;
            _mainInterface.Show();
        }

        if (_mainInterface.WindowState == WindowState.Minimized)
        {
            _mainInterface.WindowState = WindowState.Normal;
        }
        MainInterfaceWindow window = _mainInterface;
        window.Activate();
        Dispatcher.BeginInvoke(() =>
        {
            if (_mainInterface == window)
            {
                window.Activate();
            }
        });
    }

    internal void SetScenario(DesktopScenario scenario)
    {
        Scenario = scenario;
        foreach (PetEntry pet in _pets)
        {
            pet.Window.SetScenario(scenario);
        }
        _mainInterface?.Refresh();
    }

    internal void RecordVoice(string characterId, PetVoicePlayer.VoiceLine line)
    {
        _recentVoices.RemoveAll(voice => voice.CharacterId == characterId && voice.LineId == line.Id);
        _recentVoices.Insert(0, new VoiceFavorite(characterId, line.Id, line.Subtitle));
        if (_recentVoices.Count > 20)
        {
            _recentVoices.RemoveAt(20);
        }
        _mainInterface?.RefreshVoiceHistory();
    }

    internal void ToggleFavorite(VoiceFavorite voice)
    {
        VoiceFavorite[] remaining = _settings.VoiceFavorites
            .Where(item => item.CharacterId != voice.CharacterId || item.LineId != voice.LineId).ToArray();
        UpdateSettings(_settings with
        {
            VoiceFavorites = remaining.Length == _settings.VoiceFavorites.Length ? [.. remaining, voice] : remaining
        });
    }

    internal void UpdateSettings(PetSettings settings, bool applyToPets = false)
    {
        _settings = settings;
        foreach (PetEntry pet in _pets)
        {
            if (applyToPets)
            {
                pet.Window.ApplySettings(settings);
            }
            else
            {
                pet.Window.ApplyVoiceFavorites(settings);
            }
        }

        SaveSettings();
        if (applyToPets)
        {
            _mainInterface?.RefreshVoiceAssets();
        }
        _mainInterface?.RefreshVoiceHistory();
    }

    internal void OpenWardrobe(PetEntry pet)
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

        var wardrobe = new WardrobeWindow(pet.Session, _installedPacks, () => _settings.HardwareAccelerationEnabled)
        {
            Icon = pet.Window.Icon
        };
        pet.Wardrobe = wardrobe;
        wardrobe.Closed += (_, _) => pet.Wardrobe = null;
        wardrobe.ShowNear(pet.Window);
    }

    internal void ExitApplication()
    {
        if (!_exiting)
        {
            SavePendingDesktop();
            _exiting = true;
            Shutdown();
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        SavePendingDesktop();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _desktopSave?.Stop();
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

    internal sealed class PetEntry(int slot, PetWindow window, PetSession session)
    {
        internal int Slot { get; } = slot;
        internal PetWindow Window { get; } = window;
        internal PetSession Session { get; } = session;
        internal LoadedOutfit? Displayed { get; set; }
        internal WardrobeWindow? Wardrobe { get; set; }
    }
}
