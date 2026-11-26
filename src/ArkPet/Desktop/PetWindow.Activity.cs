using System;
using System.Drawing;
using System.Linq;
using System.Windows.Interop;
using Screen = System.Windows.Forms.Screen;
using WindowBounds = ArtPet.Windows.Desktop.NativeMethods.WindowBounds;

namespace ArtPet.Windows.Desktop;

internal enum DesktopScenario { Daily, Focus, Hidden }
internal enum ActivityScope { CurrentDisplay, SelectedDisplay, Corner }
internal enum ActivityCorner { TopLeft, TopRight, BottomLeft, BottomRight }
internal sealed record ActivityArea(ActivityScope Scope = ActivityScope.CurrentDisplay,
    string? Display = null, ActivityCorner Corner = ActivityCorner.BottomRight);

public partial class PetWindow
{
    private bool _pairReaction;
    internal bool AllowPetInteraction { get; private set; }
    internal bool CanInteractWithPet => AllowPetInteraction && Settings.InteractionEnabled && IsVisible &&
        HasOutfit && _press is null && !ContextMenu.IsOpen && !_player.IsReacting && !_voice.IsPlaying;
    internal System.Windows.Point? DesktopFeet
    {
        get
        {
            if (!NativeMethods.GetWindowRect(_handle, out WindowBounds bounds))
            {
                return null;
            }
            PointF feet = GetFeet(bounds);
            return new System.Windows.Point(feet.X, feet.Y);
        }
    }

    internal void SetPetInteraction(bool allowed)
    {
        AllowPetInteraction = allowed;
        DesktopStateChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void InteractWithPet(double offsetX)
    {
        FaceDirection(offsetX);
        if (_player.HasTouchAnimation)
        {
            _pairReaction = true;
            _player.React();
        }
    }

    private void FaceDirection(double offsetX)
    {
        double facing = (offsetX < 0) != _player.MoveFacesLeft ? -1 : 1;
        if (offsetX == 0 || FacingTransform.ScaleX == facing ||
            !NativeMethods.GetWindowRect(_handle, out WindowBounds bounds))
        {
            return;
        }

        float x = GetFeet(bounds).X;
        FacingTransform.ScaleX = facing;
        MovePet(bounds, bounds.Left + (int)Math.Round(x - GetFeet(bounds).X), bounds.Top);
    }

    internal ActivityArea Activity { get; private set; } = new();
    internal bool IsWithinActivityArea => NativeMethods.GetWindowRect(_handle, out WindowBounds bounds) &&
        ContainsFeet(GetActivityArea(out _), GetFeet(bounds));
    internal bool UsesFallbackActivityDisplay
    {
        get
        {
            GetActivityArea(out bool fallback);
            return fallback;
        }
    }

    internal void SetActivityArea(ActivityArea area)
    {
        Activity = area;
        PauseRoaming();
        DesktopStateChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void ReturnToActivityArea()
    {
        if (!HasOutfit)
        {
            return;
        }

        new WindowInteropHelper(this).EnsureHandle();
        RecoverPlacement();
        Rectangle area = GetActivityArea(out _);
        if (NativeMethods.GetWindowRect(_handle, out WindowBounds bounds))
        {
            PointF feet = GetFeet(bounds);
            MoveFeetTo(Math.Clamp(feet.X, area.Left + 1, area.Right - 1),
                Math.Clamp(feet.Y, area.Top + 1, area.Bottom - 1));
        }
    }

    internal DesktopPosition? CaptureDesktopPosition()
    {
        if (!NativeMethods.GetWindowRect(_handle, out WindowBounds bounds))
        {
            return null;
        }
        PointF feet = GetFeet(bounds);
        Screen screen = Screen.FromPoint(Point.Round(feet));
        Rectangle work = WorkingArea(screen);
        return new DesktopPosition(screen.DeviceName,
            Math.Round(((double)feet.X - work.Left) / Math.Max(1, work.Width), 5),
            Math.Round(((double)feet.Y - work.Top) / Math.Max(1, work.Height), 5));
    }

    internal void RestoreDesktopPosition(DesktopPosition position)
    {
        Screen screen = Screen.AllScreens.FirstOrDefault(display => display.DeviceName == position.Display)
            ?? Screen.PrimaryScreen ?? Screen.FromPoint(Point.Empty);
        Rectangle work = WorkingArea(screen);
        new WindowInteropHelper(this).EnsureHandle();
        double x = work.Left + Math.Clamp(position.X, 0, 1) * work.Width;
        double y = work.Top + Math.Clamp(position.Y, 0, 1) * work.Height;
        MoveFeetTo(x, y);
        if (NativeMethods.GetWindowRect(_handle, out WindowBounds bounds))
        {
            // Keep the visible sprite inside the new work area even when window padding extends beyond it.
            var artwork = GetArtworkRect(bounds);
            double dx = Math.Clamp(artwork.Left, work.Left, Math.Max(work.Left, work.Right - artwork.Width)) - artwork.Left;
            double dy = Math.Clamp(artwork.Top, work.Top, Math.Max(work.Top, work.Bottom - artwork.Height)) - artwork.Top;
            MovePet(bounds, bounds.Left + (int)Math.Round(dx), bounds.Top + (int)Math.Round(dy));
        }
    }

    private static bool ContainsFeet(Rectangle area, PointF feet) =>
        feet.X >= area.Left && feet.X <= area.Right && feet.Y >= area.Top && feet.Y <= area.Bottom;

    private Rectangle GetActivityArea(out bool fallback)
    {
        Screen? screen = Activity.Scope == ActivityScope.CurrentDisplay
            ? Screen.FromHandle(_handle)
            : Screen.AllScreens.FirstOrDefault(display => display.DeviceName == Activity.Display);
        fallback = screen is null;
        screen ??= Screen.PrimaryScreen ?? Screen.FromHandle(_handle);
        Rectangle area = WorkingArea(screen);
        if (Activity.Scope != ActivityScope.Corner)
        {
            return area;
        }

        int centerX = area.Left + area.Width / 2;
        int centerY = area.Top + area.Height / 2;
        bool right = Activity.Corner is ActivityCorner.TopRight or ActivityCorner.BottomRight;
        bool bottom = Activity.Corner is ActivityCorner.BottomLeft or ActivityCorner.BottomRight;
        return Rectangle.FromLTRB(right ? centerX : area.Left, bottom ? centerY : area.Top,
            right ? area.Right : centerX, bottom ? area.Bottom : centerY);
    }

    private static Rectangle WorkingArea(Screen screen) =>
        NativeMethods.GetWorkingArea(screen.Bounds.Location) ?? screen.WorkingArea;

    private PointF GetFeet(WindowBounds bounds)
    {
        var anchor = GetResizeAnchor();
        double padding = _effectPadding * NativeMethods.GetDpiForWindow(_handle) / 96.0;
        return new PointF((float)(bounds.Left + padding + (bounds.Width - 2 * padding) * anchor.X),
            (float)(bounds.Top + padding + (bounds.Height - 2 * padding) * anchor.Y));
    }

    private void MoveFeetTo(double x, double y)
    {
        PauseRoaming();
        ResetPhysicsPosition();
        _atPrimaryCorner = false;
        // Moving across monitors can resize the HWND. Reapply the anchor after the DPI change.
        for (int attempt = 0; attempt < 2 && NativeMethods.GetWindowRect(_handle, out WindowBounds bounds); attempt++)
        {
            PointF feet = GetFeet(bounds);
            MovePet(bounds, bounds.Left + (int)Math.Round(x - feet.X),
                bounds.Top + (int)Math.Round(y - feet.Y));
        }
        DesktopStateChanged?.Invoke(this, EventArgs.Empty);
    }
}
