using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using ArtPet.Windows.Desktop;
using ArtPet.Windows.Media;
using ArtPet.Windows.Wardrobe;

namespace ArtPet.Windows;

public partial class App : Application
{
    private Mutex? _instance;
    private TrayIcon? _tray;
    private PetSession? _session;
    private WardrobeWindow? _wardrobe;
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

        var pet = new PetWindow();
        MainWindow = pet;
        pet.Closed += (_, _) => Shutdown();
        var settingsStore = new PetSettingsStore(Path.Combine(ProjectStorage.DataDirectory, "settings.json"));
        pet.ApplySettings(settingsStore.Load());
        pet.SettingsSelected += settings =>
        {
            if (!settingsStore.TrySave(settings))
            {
                _tray?.ShowSettingsSaveError();
            }
        };

        CharacterPack[] installedPacks = CharacterPack.FindInstalled().ToArray();
        var session = new PetSession();
        _session = session;
        foreach (CharacterPack pack in installedPacks)
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

        Action? openWardrobe = null;
        if (session is { Pack: { } currentPack, Outfit: { } displayed })
        {
            pet.SetVoiceCharacter(currentPack.Id);
            pet.SetOutfit(displayed);
            openWardrobe = () => OpenWardrobe(pet, session, installedPacks);
            pet.AddWardrobeCommand(openWardrobe);

            session.Changed += (_, _) =>
            {
                if (session is { Pack: { } selectedPack, Outfit: { } outfit } && outfit != displayed)
                {
                    displayed = outfit;
                    pet.SetVoiceCharacter(selectedPack.Id);
                    pet.SetOutfit(outfit);
                }
            };
        }

        Action openSettings = () => OpenSettings(pet);
        pet.AddSettingsCommand(openSettings);
        _tray = new TrayIcon(pet, () => Shutdown(), openSettings, openWardrobe);
        pet.ShowPet();
    }

    private void OpenSettings(PetWindow pet)
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

        var settingsWindow = new SettingsWindow(pet.Settings);
        _settingsWindow = settingsWindow;
        settingsWindow.SettingsSelected += pet.ChooseSettings;
        settingsWindow.Closed += (_, _) => _settingsWindow = null;
        settingsWindow.Show();
    }

    private void OpenWardrobe(PetWindow pet, PetSession session, IReadOnlyList<CharacterPack> installedPacks)
    {
        if (_wardrobe is { } open)
        {
            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }

            open.Activate();
            return;
        }

        var wardrobe = new WardrobeWindow(session, installedPacks);
        _wardrobe = wardrobe;
        wardrobe.Closed += (_, _) => _wardrobe = null;
        wardrobe.ShowNear(pet);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _session?.Dispose();
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
