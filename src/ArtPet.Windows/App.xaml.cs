using System;
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

        foreach (CharacterPack pack in CharacterPack.FindInstalled())
        {
            var candidate = new PetSession(pack);
            _session = candidate;
            var startupOrder = pack.Outfits.Where(outfit => outfit != pack.DefaultOutfit).Prepend(pack.DefaultOutfit);
            foreach (OutfitDefinition outfit in startupOrder)
            {
                await candidate.SelectAsync(outfit);
                if (_exiting)
                {
                    return;
                }

                if (candidate.Outfit is not null)
                {
                    break;
                }
            }

            if (candidate.Outfit is not null)
            {
                break;
            }

            candidate.Dispose();
            _session = null;
        }

        Action? openWardrobe = null;
        if (_session is { Outfit: { } displayed } session)
        {
            pet.SetOutfit(displayed);
            openWardrobe = () => OpenWardrobe(pet, session);
            pet.AddWardrobeCommand(openWardrobe);

            session.Changed += (_, _) =>
            {
                if (session.Outfit is { } outfit && outfit != displayed)
                {
                    displayed = outfit;
                    pet.SetOutfit(outfit);
                }
            };
        }

        _tray = new TrayIcon(pet, () => Shutdown(), openWardrobe);
        pet.ShowPet();
    }

    private void OpenWardrobe(PetWindow pet, PetSession session)
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

        var wardrobe = new WardrobeWindow(session);
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
