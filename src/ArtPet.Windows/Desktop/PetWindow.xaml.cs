using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;
using Screen = System.Windows.Forms.Screen;
using WindowBounds = ArtPet.Windows.Desktop.NativeMethods.WindowBounds;

namespace ArtPet.Windows.Desktop;

public partial class PetWindow : Window
{
    private const int CornerMarginDip = 16;
    private readonly DispatcherTimer _gestureTimer;
    private HwndSource? _source;
    private IntPtr _handle;
    private string? _monitorName;
    private bool _atPrimaryCorner = true;
    private Press? _press;
    private bool _dragging;

    public PetWindow()
    {
        InitializeComponent();

        _gestureTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _gestureTimer.Tick += PollGesture;
        IsVisibleChanged += UpdateVisibility;
    }

    public void ShowPet(bool resetPosition = false)
    {
        StopGesture();
        new WindowInteropHelper(this).EnsureHandle();
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
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
    }

    protected override void OnClosed(EventArgs e)
    {
        StopGesture();
        PetArt.Stop();
        SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        _source?.RemoveHook(WindowMessage);
        _handle = IntPtr.Zero;
        base.OnClosed(e);
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == NativeMethods.MouseActivate)
        {
            handled = true;
            return NativeMethods.NoActivate;
        }

        if (message == NativeMethods.DpiChanged ||
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
            if (_handle != IntPtr.Zero && IsVisible && _press is null)
            {
                RecoverPlacement();
            }
        });
    }

    private void RecoverPlacement()
    {
        if (!NativeMethods.GetWindowRect(_handle, out WindowBounds bounds))
        {
            return;
        }

        Screen? monitor = _atPrimaryCorner
            ? null
            : Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName == _monitorName);

        if (monitor is not null)
        {
            MoveWithinWorkArea(monitor, GetWorkingArea(monitor), bounds, bounds.Left, bounds.Top);
            return;
        }

        monitor = Screen.PrimaryScreen;
        if (monitor is not null)
        {
            // Keep the corner anchor through the DPI change if this move crosses monitors.
            _atPrimaryCorner = true;
            int margin = (int)Math.Round(CornerMarginDip * NativeMethods.GetDpiForWindow(_handle) / 96.0);
            Rectangle work = GetWorkingArea(monitor);
            MoveWithinWorkArea(monitor, work, bounds, work.Right - bounds.Width - margin, work.Bottom - bounds.Height - margin);
        }
    }

    private static Rectangle GetWorkingArea(Screen monitor) =>
        NativeMethods.GetWorkingArea(monitor.Bounds.Location) ?? monitor.WorkingArea;

    private void MoveWithinWorkArea(Screen monitor, Rectangle work, WindowBounds bounds, int x, int y)
    {
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - bounds.Width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - bounds.Height));

        _monitorName = monitor.DeviceName;
        if (x != bounds.Left || y != bounds.Top)
        {
            NativeMethods.MoveWindow(_handle, x, y);
        }
    }

    private void StartGesture(object sender, MouseButtonEventArgs e)
    {
        if (_press is not null || !NativeMethods.GetCursorPos(out Point cursor) ||
            !NativeMethods.GetWindowRect(_handle, out WindowBounds bounds) || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        uint dpi = NativeMethods.GetDpiForWindow(_handle);
        int primaryButton = NativeMethods.GetSystemMetrics(NativeMethods.SwapButton) == 0
            ? NativeMethods.LeftButton : NativeMethods.RightButton;

        _press = new Press(cursor, (double)(cursor.X - bounds.Left) / bounds.Width,
            (double)(cursor.Y - bounds.Top) / bounds.Height, primaryButton,
            NativeMethods.GetSystemMetricsForDpi(NativeMethods.DragWidth, dpi),
            NativeMethods.GetSystemMetricsForDpi(NativeMethods.DragHeight, dpi));

        // A background window cannot reliably capture input beyond its visible pixels.
        _gestureTimer.Start();
        e.Handled = true;
    }

    private void PollGesture(object? sender, EventArgs e)
    {
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
        RecoverPlacement();

        if (touched)
        {
            PetArt.React();
        }
    }

    private void UpdateDrag(Press press, Point cursor)
    {
        _dragging |= Math.Abs(cursor.X - press.Start.X) >= press.DragWidth ||
            Math.Abs(cursor.Y - press.Start.Y) >= press.DragHeight;

        if (_dragging && NativeMethods.GetWindowRect(_handle, out WindowBounds bounds))
        {
            _atPrimaryCorner = false;
            Screen monitor = Screen.FromPoint(cursor);
            MoveWithinWorkArea(monitor, GetWorkingArea(monitor), bounds,
                cursor.X - (int)Math.Round(press.GrabX * bounds.Width),
                cursor.Y - (int)Math.Round(press.GrabY * bounds.Height));
        }
    }

    private void StopGesture()
    {
        _gestureTimer.Stop();
        _press = null;
        _dragging = false;
    }

    private void UpdateVisibility(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            PetArt.Start();
        }
        else
        {
            StopGesture();
            PetArt.Stop();
        }
    }

    private void HidePet(object sender, RoutedEventArgs e) => Hide();

    private sealed record Press(Point Start, double GrabX, double GrabY, int PrimaryButton, int DragWidth, int DragHeight);
}
