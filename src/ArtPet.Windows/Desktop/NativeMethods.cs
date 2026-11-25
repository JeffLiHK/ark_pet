using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

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
    private const int NoActivateStyle = 0x08000000;
    private const int ToolWindowStyle = 0x00000080;
    private const int ExtendedFrameBounds = 9;
    private const int Cloaked = 14;

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

    internal static void SetPassiveInput(IntPtr handle, bool passThrough)
    {
        // A layered window with WS_EX_TRANSPARENT passes mouse input through at every pixel.
        int style = GetWindowLongW(handle, ExtendedStyle);
        int newStyle = (passThrough ? style | TransparentStyle : style & ~TransparentStyle) | NoActivateStyle;
        if (newStyle != style)
        {
            SetWindowLongW(handle, ExtendedStyle, newStyle);
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
                NoSize | NoMove | NoZOrder | NoActivateOnPosition | FrameChanged);
        }
    }

    internal static void SetPassivePopup(IntPtr handle, bool topmost)
    {
        SetPassiveInput(handle, passThrough: true);
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

    internal static List<DesktopWindow> GetDesktopWindows()
    {
        var windows = new List<DesktopWindow>();
        var className = new StringBuilder(256);
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || IsIconic(handle))
            {
                return true;
            }

            GetWindowThreadProcessId(handle, out uint processId);
            if (processId == Environment.ProcessId ||
                (DwmGetWindowAttribute(handle, Cloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0) ||
                GetClassNameW(handle, className, className.Capacity) == 0)
            {
                return true;
            }

            string name = className.ToString();
            bool taskbar = name is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
            if (!taskbar && (name is "Progman" or "WorkerW" ||
                (GetWindowLongW(handle, ExtendedStyle) & (ToolWindowStyle | NoActivateStyle | TransparentStyle)) != 0))
            {
                return true;
            }

            // Explorer's taskbar rectangle tracks its visible position during auto-hide.
            bool hasBounds = taskbar
                ? GetWindowRect(handle, out WindowBounds bounds)
                : DwmGetWindowAttribute(handle, ExtendedFrameBounds, out bounds, Marshal.SizeOf<WindowBounds>()) == 0
                    || GetWindowRect(handle, out bounds);
            if (hasBounds && bounds.Width > 0 && bounds.Height > 0)
            {
                windows.Add(new DesktopWindow(handle, Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom),
                    taskbar ? DesktopSurfaceKind.Taskbar : DesktopSurfaceKind.Window));
            }

            return true;
        }, IntPtr.Zero);

        return windows;
    }

    internal readonly record struct DesktopWindow(IntPtr Handle, Rectangle Bounds, DesktopSurfaceKind Kind);

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
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EnumWindowsCallback(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetClassNameW(IntPtr handle, StringBuilder className, int maxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out WindowBounds value, int size);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr handle, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(IntPtr handle, int index, int value);
}
