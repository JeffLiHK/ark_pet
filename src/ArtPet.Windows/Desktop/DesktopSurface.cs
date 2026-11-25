using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;

namespace ArtPet.Windows.Desktop;

internal enum DesktopSurfaceKind
{
    Window,
    Taskbar,
    ScreenBottom
}

internal readonly record struct DesktopSurface(IntPtr Handle, DesktopSurfaceKind Kind, int Top)
{
    private const double ContactTolerance = 1;
    private const int MinimumTaskbarHeightPixels = 4;
    private static IReadOnlyList<NativeMethods.DesktopWindow> _windows = [];
    private static long? _lastScan;

    internal static DesktopSurface? FindSupport(Rectangle display, int x, double fromY, double toY,
        double minimumTop, DesktopSurface? previous, long now)
    {
        // All pets share a single desktop snapshot on the WPF dispatcher.
        if (_lastScan is not { } last || Stopwatch.GetElapsedTime(last, now).TotalMilliseconds >= 75)
        {
            _windows = NativeMethods.GetDesktopWindows();
            _lastScan = now;
        }

        return FindSupport(_windows, display, x, fromY, toY, minimumTop, previous);
    }

    internal static DesktopSurface? FindSupport(IReadOnlyList<NativeMethods.DesktopWindow> windows,
        Rectangle display, int x, double fromY, double toY, double minimumTop, DesktopSurface? previous)
    {
        DesktopSurface? hit = toY >= display.Bottom - ContactTolerance
            ? new DesktopSurface(IntPtr.Zero, DesktopSurfaceKind.ScreenBottom, display.Bottom)
            : null;

        for (int index = 0; index < windows.Count; index++)
        {
            var window = windows[index];
            var bounds = window.Bounds;
            if (x < bounds.Left || x >= bounds.Right || bounds.Top < minimumTop ||
                bounds.Top >= display.Bottom ||
                (window.Kind == DesktopSurfaceKind.Taskbar &&
                    Rectangle.Intersect(display, bounds).Height < MinimumTaskbarHeightPixels))
            {
                continue;
            }

            // Keep contact if the same edge is still underneath us and moves up.
            // A descending edge is caught by gravity, just like any other platform.
            bool retained = previous?.Handle == window.Handle && bounds.Top <= fromY + ContactTolerance;
            bool taskbarRevealed = previous?.Kind == DesktopSurfaceKind.ScreenBottom &&
                window.Kind == DesktopSurfaceKind.Taskbar && bounds.Bottom >= display.Bottom;
            bool crossed = bounds.Top >= fromY - ContactTolerance && bounds.Top <= toY;
            if ((!retained && !crossed && !taskbarRevealed) || (hit is { } firstHit && bounds.Top >= firstHit.Top) ||
                IsCovered(windows, index, x, bounds.Top))
            {
                continue;
            }

            hit = new DesktopSurface(window.Handle, window.Kind, bounds.Top);
        }

        return hit;
    }

    private static bool IsCovered(IReadOnlyList<NativeMethods.DesktopWindow> windows, int index, int x, int y)
    {
        for (int above = 0; above < index; above++)
        {
            if (windows[above].Bounds.Contains(x, y))
            {
                return true;
            }
        }

        return false;
    }
}
