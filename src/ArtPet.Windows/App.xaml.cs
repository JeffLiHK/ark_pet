using System;
using System.Threading;
using System.Windows;
using ArtPet.Windows.Desktop;

namespace ArtPet.Windows;

public partial class App : Application
{
    private Mutex? _instance;
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
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
        _tray = new TrayIcon(pet, () => Shutdown());
        pet.ShowPet();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
