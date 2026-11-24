using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ArtPet.Windows.Desktop;

internal partial class SettingsWindow : Window
{
    private PetSettings _settings;

    internal SettingsWindow(PetSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        SizeOptions.ItemsSource = PetSettings.Sizes.ToArray();
        SizeOptions.SelectedItem = settings.SizePercent;
        FrameRateOptions.ItemsSource = PetSettings.FrameRates.ToArray();
        FrameRateOptions.SelectedItem = settings.AnimationFps;
        IdleLoopsOptions.ItemsSource = Enumerable.Range(
            PetSettings.MinIdleLoopsPerAction,
            PetSettings.MaxIdleLoopsPerAction - PetSettings.MinIdleLoopsPerAction + 1);
        IdleLoopsOptions.SelectedItem = settings.IdleLoopsPerAction;
        InteractionModeCheckBox.IsChecked = settings.InteractionEnabled;

        SizeOptions.SelectionChanged += ApplySelections;
        FrameRateOptions.SelectionChanged += ApplySelections;
        IdleLoopsOptions.SelectionChanged += ApplySelections;
        InteractionModeCheckBox.Click += ApplySelections;
    }

    internal event Action<PetSettings>? SettingsSelected;

    private void ApplySelections(object sender, RoutedEventArgs e)
    {
        if (SizeOptions.SelectedItem is int size && FrameRateOptions.SelectedItem is int frameRate &&
            IdleLoopsOptions.SelectedItem is int idleLoops)
        {
            var settings = _settings with
            {
                SizePercent = size,
                AnimationFps = frameRate,
                IdleLoopsPerAction = idleLoops,
                InteractionEnabled = InteractionModeCheckBox.IsChecked == true
            };
            if (settings != _settings)
            {
                _settings = settings;
                SettingsSelected?.Invoke(settings);
            }
        }
    }

    private void CloseOnEscape(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
