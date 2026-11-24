using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ArtPet.Windows.Desktop;

internal static class NativeMethods
{
    internal const int MouseActivate = 0x0021;
    internal const int NoActivate = 3;
    internal const int SettingChange = 0x001A;
    internal const int SetWorkArea = 0x002F;
    internal const int DpiChanged = 0x02E0;

    internal const int LeftButton = 0x01;
    internal const int RightButton = 0x02;
    internal const int SwapButton = 23;
    internal const int DragWidth = 68;
    internal const int DragHeight = 69;

    private const uint NoSize = 0x0001;
    private const uint NoMove = 0x0002;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivateOnPosition = 0x0010;
    private const uint FrameChanged = 0x0020;
    private const uint DeferErase = 0x2000;
    private const uint MonitorDefaultToNearest = 0x0002;
    private const int ExtendedStyle = -20;
    private const int TransparentStyle = 0x00000020;

    internal static bool MoveWindow(IntPtr handle, int x, int y, bool deferErase = false)
    {
        // During continuous moves, avoid WM_SYNCPAINT to other windows. WPF still receives its position messages.
        uint flags = NoSize | NoZOrder | NoActivateOnPosition | (deferErase ? DeferErase : 0);
        return SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, flags);
    }

    internal static void BringForward(IntPtr handle)
    {
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, NoSize | NoMove | NoActivateOnPosition);
    }

    internal static void SetMousePassThrough(IntPtr handle, bool enabled)
    {
        // A layered window with WS_EX_TRANSPARENT passes mouse input through at every pixel.
        int style = GetWindowLongW(handle, ExtendedStyle);
        int newStyle = enabled ? style | TransparentStyle : style & ~TransparentStyle;
        if (newStyle != style)
        {
            SetWindowLongW(handle, ExtendedStyle, newStyle);
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
                NoSize | NoMove | NoZOrder | NoActivateOnPosition | FrameChanged);
        }
    }

    internal static void SetPassivePopup(IntPtr handle, bool topmost)
    {
        SetMousePassThrough(handle, enabled: true);
        SetWindowPos(handle, new IntPtr(topmost ? -1 : -2), 0, 0, 0, 0, NoSize | NoMove | NoActivateOnPosition);
    }

    internal static Rectangle? GetWorkingArea(Point monitorOrigin)
    {
        IntPtr monitor = MonitorFromPoint(monitorOrigin, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info))
        {
            return null;
        }

        WindowBounds area = info.WorkArea;
        return Rectangle.FromLTRB(area.Left, area.Top, area.Right, area.Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowBounds
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public WindowBounds MonitorArea;
        public WindowBounds WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr handle, out WindowBounds bounds);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    internal static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr handle, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(IntPtr handle, int index, int value);
}
