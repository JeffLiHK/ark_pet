using System;
using ArtPet.Windows.Media;
using Point = System.Drawing.Point;
using Screen = System.Windows.Forms.Screen;
using WindowBounds = ArtPet.Windows.Desktop.NativeMethods.WindowBounds;

namespace ArtPet.Windows.Desktop;

public partial class PetWindow
{
    private const double GravityDip = 1400;
    private const double TerminalSpeedDip = 1800;
    private double _fallSpeedDip;
    private double _fallRemainder;
    private int? _lastPhysicsTop;
    private DesktopSurface? _support;

    private void ResetPhysicsPosition()
    {
        _fallSpeedDip = 0;
        _fallRemainder = 0;
        _lastPhysicsTop = null;
        _support = null;
    }

    private void UpdatePhysics(double elapsed, long now)
    {
        if (!NativeMethods.GetWindowRect(_handle, out WindowBounds bounds))
        {
            ResetPhysicsPosition();
            return;
        }

        if (_lastPhysicsTop != bounds.Top)
        {
            ResetPhysicsPosition();
        }

        double dpiScale = NativeMethods.GetDpiForWindow(_handle) / 96.0;
        double padding = _effectPadding * dpiScale;
        double width = bounds.Width - 2 * padding;
        double height = bounds.Height - 2 * padding;
        double anchorX = FacingTransform.ScaleX < 0 ? 1 - _scaleAnchor.X : _scaleAnchor.X;
        int contactX = bounds.Left + (int)Math.Round(padding + anchorX * width);
        double contactOffset = padding + _artworkBounds.Bottom * height;
        double contactY = bounds.Top + contactOffset + _fallRemainder;
        var display = Screen.FromPoint(new Point(contactX, (int)Math.Round(bounds.Top + padding + _artworkBounds.Top * height))).Bounds;
        int surfaceX = Math.Clamp(contactX, display.Left, display.Right - 1);
        // A frame at the top of the monitor cannot support the pet if its artwork would be clipped.
        double minimumTop = display.Top + _artworkBounds.Height * height;
        double nextSpeed = Math.Min(TerminalSpeedDip, _fallSpeedDip + GravityDip * elapsed);
        double nextY = contactY + (_fallSpeedDip + nextSpeed) * 0.5 * elapsed * dpiScale;

        _support = DesktopSurface.FindSupport(display, surfaceX, contactY, nextY, minimumTop,
            _support, now);
        double targetY = (_support is { } support ? support.Top : nextY) - contactOffset;
        int top = (int)Math.Round(targetY);
        int left = bounds.Left + surfaceX - contactX;
        if ((top != bounds.Top || left != bounds.Left) &&
            !NativeMethods.MoveWindow(_handle, left, top, deferErase: true))
        {
            ResetPhysicsPosition();
            return;
        }

        _lastPhysicsTop = top;
        _fallRemainder = targetY - top;
        _fallSpeedDip = _support is null ? nextSpeed : 0;
        if (_support is not null || top != bounds.Top || left != bounds.Left)
        {
            _atPrimaryCorner = false;
        }
    }

    private RoamingAction GetPhysicsAction()
    {
        if (!Settings.PhysicsModeEnabled)
        {
            return RoamingAction.Idle;
        }

        return _support?.Kind switch
        {
            DesktopSurfaceKind.Window => RoamingAction.Sit,
            DesktopSurfaceKind.Taskbar when !WalkEnabled || !_player.CanWalk => RoamingAction.Sleep,
            _ => RoamingAction.Idle
        };
    }
}
