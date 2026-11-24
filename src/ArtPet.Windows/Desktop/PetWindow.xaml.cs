using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ArtPet.Windows.Media;
using Microsoft.Win32;
using Point = System.Drawing.Point;
using Screen = System.Windows.Forms.Screen;
using ScaleAnchor = System.Windows.Point;
using WindowBounds = ArtPet.Windows.Desktop.NativeMethods.WindowBounds;

namespace ArtPet.Windows.Desktop;

public partial class PetWindow : Window
{
    private const int CornerMarginDip = 16;
    private const double DefaultSizeDip = 160;
    private static readonly TimeSpan MinimumGestureInterval = TimeSpan.FromMilliseconds(8);
    // The resting artwork inside SproutVisual's 160 by 160 viewbox.
    private static readonly Rect SproutArtwork = new(26 / 160.0, 34 / 160.0, 108 / 160.0, 116 / 160.0);
    private readonly FramePlayer _player;
    private readonly PetVoicePlayer _voice;
    private HwndSource? _source;
    private IntPtr _handle;
    private bool _atPrimaryCorner = true;
    private Press? _press;
    private DispatcherOperation? _gestureUpdate;
    private long _lastGestureUpdate;
    private bool _dragging;
    private bool _placementCheckPending;
    private ScaleAnchor _scaleAnchor = new(0.5, 0.91);
    private Rect _artworkBounds = SproutArtwork;

    public PetWindow()
    {
        InitializeComponent();
        _player = new FramePlayer(CharacterImage, Breeze);
        _voice = new PetVoicePlayer(SetVoiceSubtitle);
        VoiceBubble.CustomPopupPlacementCallback = PlaceVoiceBubble;
        // An open WPF popup does not follow its placement target when the window moves.
        LocationChanged += (_, _) => _voice.Stop();

        IsVisibleChanged += UpdateVisibility;

        ApplySettings(Settings);
    }

    internal event Action<PetSettings>? SettingsSelected;

    internal PetSettings Settings { get; private set; } = new();

    internal void ApplySettings(PetSettings settings)
    {
        bool interactionChanged = Settings.InteractionEnabled != settings.InteractionEnabled;
        Settings = settings;
        if (interactionChanged)
        {
            ApplyInteractionMode();
        }

        _player.FramesPerSecond = settings.AnimationFps;
        _player.IdleLoopsPerAction = settings.IdleLoopsPerAction;
        PetArt.FramesPerSecond = settings.AnimationFps;
        ResizePet(DefaultSizeDip * settings.SizePercent / PetSettings.DefaultSize);
    }

    internal void ChooseSettings(PetSettings settings)
    {
        ApplySettings(settings);
        SettingsSelected?.Invoke(settings);
    }

    private void ApplyInteractionMode()
    {
        bool enabled = Settings.InteractionEnabled;
        ContextMenuService.SetIsEnabled(this, enabled);

        if (_handle != IntPtr.Zero)
        {
            NativeMethods.SetMousePassThrough(_handle, !enabled);
        }

        if (!enabled)
        {
            StopGesture();
            ContextMenu.IsOpen = false;
            _voice.Stop();
            _player.CancelReaction();
            PetArt.CancelReaction();
        }
    }

    internal void SetVoiceCharacter(string characterId) => _voice.SetCharacter(characterId);

    internal void SetOutfit(LoadedOutfit outfit)
    {
        _voice.Stop();
        _scaleAnchor = outfit.Anchor;
        Rect artwork = ArtworkBounds.FromFrame(outfit.Idle.Frames[0]);
        // An idle clip can start with a blank frame.
        _artworkBounds = artwork.IsEmpty ? new Rect(0, 0, 1, 1) : artwork;
        PetArt.Stop();
        PetArt.Visibility = Visibility.Collapsed;
        _player.SetOutfit(outfit, IsVisible);
        if (IsVisible)
        {
            QueuePlacementCheck();
        }
    }

    internal void AddWardrobeCommand(Action open)
    {
        WardrobeItem.Visibility = Visibility.Visible;
        WardrobeItem.Click += (_, _) => open();
    }

    internal void AddSettingsCommand(Action open) => SettingsItem.Click += (_, _) => open();

    public void ShowPet(bool resetPosition = false)
    {
        StopGesture();
        new WindowInteropHelper(this).EnsureHandle();
        NativeMethods.SetMousePassThrough(_handle, !Settings.InteractionEnabled);
        if (resetPosition)
        {
            _atPrimaryCorner = true;
        }

        RecoverPlacement();
        Show();
        RecoverPlacement();
        if (!Topmost)
        {
            NativeMethods.BringForward(_handle);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source.AddHook(WindowMessage);
        NativeMethods.SetMousePassThrough(_handle, !Settings.InteractionEnabled);
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
    }

    protected override void OnClosed(EventArgs e)
    {
        StopGesture();
        _voice.Stop();
        PetArt.Stop();
        _player.Stop();
        SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        _source?.RemoveHook(WindowMessage);
        _handle = IntPtr.Zero;
        base.OnClosed(e);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == TopmostProperty && VoiceBubble?.IsOpen == true)
        {
            ConfigureVoiceBubble(this, EventArgs.Empty);
        }
    }

    private void ConfigureVoiceBubble(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(VoiceSubtitle) is HwndSource source)
        {
            NativeMethods.SetPassivePopup(source.Handle, Topmost);
        }
    }

    private static CustomPopupPlacement[] PlaceVoiceBubble(Size bubbleSize, Size targetSize, ScaleAnchor offset)
    {
        const double gap = 6;
        double x = (targetSize.Width - bubbleSize.Width) / 2;
        return
        [
            new(new ScaleAnchor(x, -bubbleSize.Height - gap), PopupPrimaryAxis.Horizontal),
            new(new ScaleAnchor(x, targetSize.Height + gap), PopupPrimaryAxis.Horizontal)
        ];
    }

    private void SetVoiceSubtitle(string? subtitle)
    {
        VoiceSubtitle.Text = subtitle ?? string.Empty;
        VoiceBubble.IsOpen = subtitle is not null && IsVisible;
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == NativeMethods.MouseActivate)
        {
            handled = true;
            return NativeMethods.NoActivate;
        }

        if ((message == NativeMethods.DpiChanged && _press is null) ||
            (message == NativeMethods.SettingChange && wParam == NativeMethods.SetWorkArea))
        {
            QueuePlacementCheck();
        }

        return IntPtr.Zero;
    }

    private void DisplaySettingsChanged(object? sender, EventArgs e) => QueuePlacementCheck();

    private void QueuePlacementCheck()
    {
        // WPF applies the suggested DPI size before the queued placement check runs.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_handle == IntPtr.Zero || !IsVisible)
            {
                return;
            }

            if (_press is null)
            {
                RecoverPlacement();
            }
            else
            {
                _placementCheckPending = true;
            }
        });
    }

    private void RecoverPlacement()
    {
        if (!NativeMethods.GetWindowRect(_handle, out WindowBounds bounds))
        {
            return;
        }

        if (!_atPrimaryCorner && ArtworkOverlapsDisplay(bounds))
        {
            return;
        }

        if (Screen.PrimaryScreen is { } primary)
        {
            // Keep the corner anchor through the DPI change if this move crosses monitors.
            _atPrimaryCorner = true;
            int margin = (int)Math.Round(CornerMarginDip * NativeMethods.GetDpiForWindow(_handle) / 96.0);
            var work = NativeMethods.GetWorkingArea(primary.Bounds.Location) ?? primary.WorkingArea;
            int x = Math.Max(work.Left, work.Right - bounds.Width - margin);
            int y = Math.Max(work.Top, work.Bottom - bounds.Height - margin);
            MovePet(bounds, x, y);
        }
    }

    private bool ArtworkOverlapsDisplay(WindowBounds bounds)
    {
        var artwork = new Rect(bounds.Left + _artworkBounds.X * bounds.Width,
            bounds.Top + _artworkBounds.Y * bounds.Height,
            _artworkBounds.Width * bounds.Width, _artworkBounds.Height * bounds.Height);

        return Screen.AllScreens.Any(screen =>
        {
            var display = screen.Bounds;
            return artwork.Left < display.Right && artwork.Right > display.Left &&
                artwork.Top < display.Bottom && artwork.Bottom > display.Top;
        });
    }

    private void ResizePet(double size)
    {
        if (Width == size && Height == size)
        {
            return;
        }

        StopGesture();
        _voice.Stop();
        WindowBounds previous = default;
        bool keepAnchor = !_atPrimaryCorner && NativeMethods.GetWindowRect(_handle, out previous);

        Width = size;
        Height = size;

        if (keepAnchor && NativeMethods.GetWindowRect(_handle, out WindowBounds resized))
        {
            int x = previous.Left + (int)Math.Round((previous.Width - resized.Width) * _scaleAnchor.X);
            int y = previous.Top + (int)Math.Round((previous.Height - resized.Height) * _scaleAnchor.Y);
            MovePet(resized, x, y);
        }

        if (_handle != IntPtr.Zero)
        {
            RecoverPlacement();
        }
    }

    private void MovePet(WindowBounds bounds, int x, int y)
    {
        if (x != bounds.Left || y != bounds.Top)
        {
            NativeMethods.MoveWindow(_handle, x, y);
        }
    }

    private void StartGesture(object sender, MouseButtonEventArgs e)
    {
        if (!Settings.InteractionEnabled || _handle == IntPtr.Zero || _press is not null ||
            !NativeMethods.GetCursorPos(out Point cursor))
        {
            return;
        }

        uint dpi = NativeMethods.GetDpiForWindow(_handle);
        int primaryButton = NativeMethods.GetSystemMetrics(NativeMethods.SwapButton) == 0
            ? NativeMethods.LeftButton : NativeMethods.RightButton;

        _press = new Press(cursor, primaryButton,
            NativeMethods.GetSystemMetricsForDpi(NativeMethods.DragWidth, dpi),
            NativeMethods.GetSystemMetricsForDpi(NativeMethods.DragHeight, dpi));

        // A background window cannot reliably capture input beyond its visible pixels.
        CompositionTarget.Rendering += QueueGestureUpdate;
        e.Handled = true;
    }

    private void QueueGestureUpdate(object? sender, EventArgs e)
    {
        // Moving the layered window may trigger a new render immediately. Bound the update rate.
        if (_press is not null && _gestureUpdate is null &&
            (_lastGestureUpdate == 0 || Stopwatch.GetElapsedTime(_lastGestureUpdate) >= MinimumGestureInterval))
        {
            // A move across monitors can resize the HWND. Do it outside the render callback.
            _gestureUpdate = Dispatcher.InvokeAsync(PollGesture, DispatcherPriority.Input);
        }
    }

    private void PollGesture()
    {
        _gestureUpdate = null;
        _lastGestureUpdate = Stopwatch.GetTimestamp();
        if (_press is not { } press || !NativeMethods.GetCursorPos(out Point cursor))
        {
            StopGesture();
            return;
        }

        if (NativeMethods.GetAsyncKeyState(press.PrimaryButton) >= 0)
        {
            CompleteGesture(press, cursor);
            return;
        }

        UpdateDrag(press, cursor);
    }

    private void EndGesture(object sender, MouseButtonEventArgs e)
    {
        if (_press is { } press && NativeMethods.GetCursorPos(out Point cursor))
        {
            CompleteGesture(press, cursor);
            e.Handled = true;
        }
    }

    private void CompleteGesture(Press press, Point cursor)
    {
        UpdateDrag(press, cursor);
        bool touched = !_dragging && NativeMethods.WindowFromPoint(cursor) == _handle;
        StopGesture();

        if (touched)
        {
            if (_player.HasOutfit)
            {
                _player.React();
                _voice.PlayRandom();
            }
            else
            {
                PetArt.React();
            }
        }
    }

    private void UpdateDrag(Press press, Point cursor)
    {
        _dragging |= Math.Abs(cursor.X - press.Start.X) >= press.DragWidth ||
            Math.Abs(cursor.Y - press.Start.Y) >= press.DragHeight;

        if (_dragging && cursor != press.LastCursor && NativeMethods.GetWindowRect(_handle, out WindowBounds bounds))
        {
            // Continue from the position WPF applied if a per-monitor DPI change resized the window.
            int x = bounds.Left + (cursor.X - press.LastCursor.X);
            int y = bounds.Top + (cursor.Y - press.LastCursor.Y);
            if (NativeMethods.MoveWindow(_handle, x, y, deferErase: true))
            {
                _atPrimaryCorner = false;
                press.LastCursor = cursor;
            }
        }
    }

    private void StopGesture()
    {
        if (_press is not null)
        {
            CompositionTarget.Rendering -= QueueGestureUpdate;
        }

        _gestureUpdate?.Abort();
        _gestureUpdate = null;
        _lastGestureUpdate = 0;
        _press = null;
        _dragging = false;
        if (_placementCheckPending)
        {
            _placementCheckPending = false;
            QueuePlacementCheck();
        }
    }

    private void UpdateVisibility(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            if (_player.HasOutfit)
            {
                _player.Start();
            }
            else
            {
                PetArt.Start();
            }
        }
        else
        {
            StopGesture();
            _voice.Stop();
            PetArt.Stop();
            _player.Stop();
        }
    }

    private void HidePet(object sender, RoutedEventArgs e) => Hide();

    private sealed record Press(Point Start, int PrimaryButton, int DragWidth, int DragHeight)
    {
        internal Point LastCursor { get; set; } = Start;
    }
}
