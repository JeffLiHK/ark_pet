using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        (_artwork, _installedPacks) = await Task.Run(() =>
        {
            AssetSnapshot? artwork = ManagedCharacterAssets.LoadInstalled(ReleaseUpdater.CurrentVersion);
            return (artwork, CharacterPack.FindInstalled(artwork).ToArray());
        });
        if (_exiting)
        {
            return;
        }

        var session = new PetSession();
        var primary = RegisterPet(session, 0);
        _primary = primary;
        MainWindow = primary.Window;
        foreach (CharacterPack pack in _installedPacks)
        {
            foreach (OutfitDefinition outfit in pack.StartupOutfits)
            {
                await session.SelectAsync(pack, outfit);
                if (_exiting)
                {
                    return;
                }

                if (session.Outfit is not null)
                {
                    break;
                }
            }

            if (session.Outfit is not null)
            {
                break;
            }
        }

        _tray = new TrayIcon(primary.Window, ExitApplication, OpenSettings, WardrobeCommand(primary),
            () => AddPet(primary), GetTrayPets, _artwork?.AppIcon);
        primary.Window.ShowPet();
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
        new TrayPet($"小人 {pet.Slot + 1} · {pet.Session.Pack?.DisplayName ?? "小芽"}", pet.Window,
            WardrobeCommand(pet), pet == _primary ? null : () => RemovePet(pet)));

    private void DisplayOutfit(PetEntry pet)
    {
        if (pet.Session is not { Pack: { } pack, Outfit: { } outfit } || pet.Displayed == outfit)
        {
            return;
        }

        pet.Displayed = outfit;
        pet.Window.SetVoiceCharacter(pack.Id);
        pet.Window.SetOutfit(outfit);
        var icon = outfit.Icon ?? (pack.Id == ArtworkCatalog.SproutId ? _artwork?.AppIcon : null) ??
            CharacterIcon.Create(outfit) ?? _artwork?.AppIcon;
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
            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }

            open.Activate();
            return;
        }

        var settingsWindow = new SettingsWindow(_settings, _artwork?.Installation)
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
