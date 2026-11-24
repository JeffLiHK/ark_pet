using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ArtPet.Windows.Desktop;
using ArtPet.Windows.Media;
using Screen = System.Windows.Forms.Screen;

namespace ArtPet.Windows.Wardrobe;

internal partial class WardrobeWindow : Window
{
    private readonly PetSession _session;
    private readonly Choice[] _choices;
    private bool _closed;

    internal WardrobeWindow(PetSession session)
    {
        InitializeComponent();
        _session = session;
        _choices = session.Pack.Outfits.Select(outfit => new Choice(outfit)).ToArray();
        CharacterName.Text = session.Pack.DisplayName;
        Outfits.ItemsSource = _choices;
        RefreshChoices(this, EventArgs.Empty);
        _session.Changed += RefreshChoices;
        Loaded += LoadPreviews;
    }

    internal void ShowNear(PetWindow pet)
    {
        IntPtr petHandle = new WindowInteropHelper(pet).Handle;
        IntPtr handle = new WindowInteropHelper(this).EnsureHandle();
        uint initialDpi = NativeMethods.GetDpiForWindow(handle);
        PlaceNear(pet, petHandle, handle);
        if (NativeMethods.GetDpiForWindow(handle) != initialDpi)
        {
            // A move to the pet's monitor can resize this window before it becomes visible.
            PlaceNear(pet, petHandle, handle);
        }

        Show();
    }

    private static void PlaceNear(PetWindow pet, IntPtr petHandle, IntPtr handle)
    {
        Screen screen = Screen.FromHandle(petHandle);
        var work = NativeMethods.GetWorkingArea(screen.Bounds.Location) ?? screen.WorkingArea;

        if (NativeMethods.GetWindowRect(handle, out var bounds) &&
            NativeMethods.GetWindowRect(petHandle, out var petBounds))
        {
            int gap = (int)Math.Round(12 * NativeMethods.GetDpiForWindow(petHandle) / 96d);
            int x = work.Left + (work.Width - bounds.Width) / 2;
            int y = work.Top + (work.Height - bounds.Height) / 2;
            if (pet.IsVisible)
            {
                x = petBounds.Left - gap - bounds.Width;
                if (x < work.Left)
                {
                    x = petBounds.Right + gap;
                }

                y = petBounds.Bottom - bounds.Height;
            }

            NativeMethods.MoveWindow(handle,
                Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - bounds.Width)),
                Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - bounds.Height)));
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _session.Changed -= RefreshChoices;
        base.OnClosed(e);
    }

    private async void LoadPreviews(object sender, RoutedEventArgs e)
    {
        Loaded -= LoadPreviews;
        BitmapSource?[] previews = await Task.Run(() => _choices.Select(choice => _session.Pack.LoadPreview(choice.Outfit)).ToArray());
        if (_closed)
        {
            return;
        }

        for (int index = 0; index < _choices.Length; index++)
        {
            _choices[index].SetPreview(previews[index]);
        }
    }

    private void RefreshChoices(object? sender, EventArgs e)
    {
        foreach (Choice choice in _choices)
        {
            choice.Refresh(_session);
        }
    }

    private async void ApplyOutfit(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Choice choice })
        {
            await _session.SelectAsync(choice.Outfit);
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

    private sealed class Choice(OutfitDefinition outfit) : INotifyPropertyChanged
    {
        internal OutfitDefinition Outfit { get; } = outfit;
        public string DisplayName => Outfit.DisplayName;
        public BitmapSource? Preview { get; private set; }
        public bool IsCurrent { get; private set; }
        public bool HasError { get; private set; }
        public string Status { get; private set; } = string.Empty;
        public string AccessibleName => string.IsNullOrEmpty(Status) ? DisplayName : $"{DisplayName}，{Status}";

        public event PropertyChangedEventHandler? PropertyChanged;

        internal void SetPreview(BitmapSource? preview)
        {
            Preview = preview;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview)));
        }

        internal void Refresh(PetSession session)
        {
            IsCurrent = session.Outfit?.Definition == Outfit;
            HasError = session.HasLoadError(Outfit);
            Status = IsCurrent ? "使用中" : session.PendingOutfit == Outfit ? "套用中…" : HasError ? "素材無法讀取" : string.Empty;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }
}
