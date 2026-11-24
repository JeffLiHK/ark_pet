using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
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
    private const int GesturePollIntervalMs = 8;
    private const int EffectPaddingDip = 16;
    // The resting artwork inside SproutVisual's 160 by 160 viewbox.
    private static readonly Rect SproutArtwork = new(26 / 160.0, 34 / 160.0, 108 / 160.0, 116 / 160.0);
    private readonly FramePlayer _player;
    private readonly PetVoicePlayer _voice;
    private readonly int _placementSlot;
    private string? _voiceCharacterId;
    private HwndSource? _source;
    private IntPtr _handle;
    private bool _atPrimaryCorner = true;
    private Press? _press;
    private Timer? _gestureTimer;
    private bool _dragging;
    private bool _placementCheckPending;
    private ScaleAnchor _scaleAnchor = new(0.5, 0.91);
    private Rect _artworkBounds = SproutArtwork;
    private int _effectPadding;

    public PetWindow() : this(0)
    {
    }

    internal PetWindow(int placementSlot)
    {
        _placementSlot = placementSlot;
        InitializeComponent();
        _player = new FramePlayer(CharacterImage, Breeze);
        _voice = new PetVoicePlayer(SetVoiceSubtitle);
        _roamTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, UpdateRoaming, Dispatcher);
        _roamTimer.Stop();
        ContextMenu.Opened += (_, _) => PauseRoaming();
        VoiceBubble.CustomPopupPlacementCallback = PlaceVoiceBubble;
        // An open WPF popup does not follow its placement target when the window moves.
        LocationChanged += (_, _) => _voice.Stop();

        IsVisibleChanged += UpdateVisibility;

        ApplySettings(Settings);
    }

    private PetSettings Settings { get; set; } = new();

    internal void ApplySettings(PetSettings settings)
    {
        bool interactionChanged = Settings.InteractionEnabled != settings.InteractionEnabled;
        bool languageChanged = Settings.VoiceLanguage != settings.VoiceLanguage ||
            Settings.SubtitleLanguage != settings.SubtitleLanguage;
        bool effectsChanged = Settings.OutlineLevel != settings.OutlineLevel || Settings.ShadowLevel != settings.ShadowLevel;
        bool freeModeChanged = Settings.FreeModeEnabled != settings.FreeModeEnabled;
        Settings = settings;
        if (interactionChanged)
        {
            ApplyInteractionMode();
        }

        if (languageChanged)
        {
            ApplyVoiceSettings();
        }

        if (effectsChanged)
        {
            ApplyArtworkEffects(settings.OutlineLevel, settings.ShadowLevel);
        }

        _voice.SetVolume(settings.VoiceVolumePercent);
        _player.FramesPerSecond = settings.AnimationFps;
        _player.IdleLoopsPerAction = settings.IdleLoopsPerAction;
        PetArt.FramesPerSecond = settings.AnimationFps;
        ResizePet(DefaultSizeDip * settings.SizePercent / PetSettings.DefaultSize);
        if (freeModeChanged)
        {
            _player.SetFreeMode(settings.FreeModeEnabled);
            RefreshRoaming();
        }
    }

    private void ApplyArtworkEffects(int outline, int shadow)
    {
        OutlineLayer.Effect = outline == 0 ? null : new DropShadowEffect
        {
            Color = Colors.White,
            ShadowDepth = 0,
            BlurRadius = 2 + outline * 2,
            Opacity = 1,
            RenderingBias = RenderingBias.Performance
        };
        ShadowLayer.Effect = shadow == 0 ? null : new DropShadowEffect
        {
            Color = Colors.Black,
            Direction = 270,
            ShadowDepth = 3,
            BlurRadius = 9,
            Opacity = 0.2 + shadow * 0.2,
            RenderingBias = RenderingBias.Performance
        };
    }

    private void ApplyInteractionMode()
    {
        bool enabled = Settings.InteractionEnabled;
        ContextMenuService.SetIsEnabled(this, enabled);

        if (_handle != IntPtr.Zero)
        {
            NativeMethods.SetPassiveInput(_handle, !enabled);
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

    internal void SetVoiceCharacter(string characterId)
    {
        if (_voiceCharacterId != characterId)
        {
            _voiceCharacterId = characterId;
            ApplyVoiceSettings();
        }
    }

    private void ApplyVoiceSettings()
    {
        VoiceSubtitle.Language = XmlLanguage.GetLanguage(Settings.SubtitleLanguage == PetSettings.Japanese ? "ja-JP" : "zh-TW");
        _voice.Configure(_voiceCharacterId, Settings.VoiceLanguage, Settings.SubtitleLanguage);
    }

    internal void SetOutfit(LoadedOutfit outfit)
    {
        PauseRoaming();
        _voice.Stop();
        _scaleAnchor = outfit.Anchor;
        Rect artwork = ArtworkBounds.FromFrame(outfit.Idle.Frames[0]);
        // An idle clip can start with a blank frame.
        _artworkBounds = artwork.IsEmpty ? new Rect(0, 0, 1, 1) : artwork;
        PetArt.Stop();
        PetArt.Visibility = Visibility.Collapsed;
        _player.SetOutfit(outfit, IsVisible);
        RefreshRoaming();
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

    internal void AddPetCommands(Action add, Action? remove)
    {
        AddPetItem.Click += (_, _) => add();
        if (remove is not null)
        {
            RemovePetItem.Visibility = Visibility.Visible;
            RemovePetItem.Click += (_, _) => remove();
        }
    }

    internal void TurnAround()
    {
        PauseRoaming();
        FacingTransform.ScaleX = -FacingTransform.ScaleX;
    }

    private void TurnPet(object sender, RoutedEventArgs e) => TurnAround();

    public void ShowPet(bool resetPosition = false)
    {
        StopGesture();
        PauseRoaming();
        new WindowInteropHelper(this).EnsureHandle();
        NativeMethods.SetPassiveInput(_handle, !Settings.InteractionEnabled);
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
        NativeMethods.SetPassiveInput(_handle, !Settings.InteractionEnabled);
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
    }

    protected override void OnClosed(EventArgs e)
    {
        StopGesture();
        _roamTimer.Stop();
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
        VoiceBubble.IsOpen = !string.IsNullOrWhiteSpace(subtitle) && IsVisible;
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
                PauseRoaming();
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
            int margin = (int)Math.Round((CornerMarginDip - _effectPadding) * NativeMethods.GetDpiForWindow(_handle) / 96.0);
            var work = NativeMethods.GetWorkingArea(primary.Bounds.Location) ?? primary.WorkingArea;
            int gap = (int)Math.Round(CornerMarginDip * NativeMethods.GetDpiForWindow(_handle) / 96.0);
            int columns = Math.Max(1, (work.Width - margin * 2 + gap) / (bounds.Width + gap));
            int rows = Math.Max(1, (work.Height - margin * 2 + gap) / (bounds.Height + gap));
            int x = Math.Max(work.Left, work.Right - bounds.Width - margin - _placementSlot % columns * (bounds.Width + gap));
            int y = Math.Max(work.Top, work.Bottom - bounds.Height - margin - (_placementSlot / columns % rows) * (bounds.Height + gap));
            MovePet(bounds, x, y);
        }
    }

    private bool ArtworkOverlapsDisplay(WindowBounds bounds)
    {
        double padding = _effectPadding * NativeMethods.GetDpiForWindow(_handle) / 96.0;
        double width = bounds.Width - 2 * padding;
        double height = bounds.Height - 2 * padding;
        double artworkX = FacingTransform.ScaleX < 0 ? 1 - _artworkBounds.Right : _artworkBounds.X;
        var artwork = new Rect(bounds.Left + padding + artworkX * width,
            bounds.Top + padding + _artworkBounds.Y * height,
            _artworkBounds.Width * width, _artworkBounds.Height * height);

        return Screen.AllScreens.Any(screen =>
        {
            var display = screen.Bounds;
            return artwork.Left < display.Right && artwork.Right > display.Left &&
                artwork.Top < display.Bottom && artwork.Bottom > display.Top;
        });
    }

    private void ResizePet(double size)
    {
        int padding = Settings.OutlineLevel > 0 || Settings.ShadowLevel > 0 ? EffectPaddingDip : 0;
        double windowSize = size + 2 * padding;
        if (Width == windowSize && Height == windowSize && _effectPadding == padding)
        {
            return;
        }

        StopGesture();
        PauseRoaming();
        _voice.Stop();
        WindowBounds previous = default;
        bool keepAnchor = !_atPrimaryCorner && NativeMethods.GetWindowRect(_handle, out previous);

        int oldPadding = _effectPadding;
        _effectPadding = padding;
        ArtworkCanvas.Margin = new Thickness(padding);
        Width = windowSize;
        Height = windowSize;

        if (keepAnchor && NativeMethods.GetWindowRect(_handle, out WindowBounds resized))
        {
            double paddingChange = (oldPadding - padding) * NativeMethods.GetDpiForWindow(_handle) / 96.0;
            double anchorX = FacingTransform.ScaleX < 0 ? 1 - _scaleAnchor.X : _scaleAnchor.X;
            int x = previous.Left + (int)Math.Round(paddingChange + (previous.Width - resized.Width - 2 * paddingChange) * anchorX);
            int y = previous.Top + (int)Math.Round(paddingChange + (previous.Height - resized.Height - 2 * paddingChange) * _scaleAnchor.Y);
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

        var press = new Press(cursor, primaryButton,
            NativeMethods.GetSystemMetricsForDpi(NativeMethods.DragWidth, dpi),
            NativeMethods.GetSystemMetricsForDpi(NativeMethods.DragHeight, dpi));
        _press = press;
        PauseRoaming();

        // Poll screen coordinates even when the pointer outruns the visible part of the window.
        _gestureTimer = new Timer(_ => QueueGestureUpdate(press), null, 0, GesturePollIntervalMs);
        e.Handled = true;
    }

    private void QueueGestureUpdate(Press press)
    {
        // Coalesce timer ticks so a busy UI always reads the latest cursor position.
        if (Interlocked.Exchange(ref press.UpdateQueued, 1) == 0)
        {
            Dispatcher.InvokeAsync(() => PollGesture(press), DispatcherPriority.Render);
        }
    }

    private async void PollGesture(Press press)
    {
        Volatile.Write(ref press.UpdateQueued, 0);
        if (!ReferenceEquals(press, _press))
        {
            return;
        }

        if (!NativeMethods.GetCursorPos(out Point cursor))
        {
            StopGesture();
            return;
        }

        if (NativeMethods.GetAsyncKeyState(press.PrimaryButton) >= 0)
        {
            await CompleteGestureAsync(press, cursor);
            return;
        }

        UpdateDrag(press, cursor);
    }

    private async void EndGesture(object sender, MouseButtonEventArgs e)
    {
        if (_press is { } press && NativeMethods.GetCursorPos(out Point cursor))
        {
            e.Handled = true;
            await CompleteGestureAsync(press, cursor);
        }
    }

    private async Task CompleteGestureAsync(Press press, Point cursor)
    {
        UpdateDrag(press, cursor);
        bool touched = !_dragging && NativeMethods.WindowFromPoint(cursor) == _handle;
        StopGesture();

        if (touched)
        {
            if (_player.HasOutfit)
            {
                _player.React();
                await _voice.PlayRandomAsync();
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
        _gestureTimer?.Dispose();
        _gestureTimer = null;
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

        RefreshRoaming();
    }

    private void HidePet(object sender, RoutedEventArgs e) => Hide();

    private sealed record Press(Point Start, int PrimaryButton, int DragWidth, int DragHeight)
    {
        internal Point LastCursor { get; set; } = Start;
        internal int UpdateQueued;
    }
}
