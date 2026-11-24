using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Point = System.Drawing.Point;
using Screen = System.Windows.Forms.Screen;
using WindowBounds = ArtPet.Windows.Desktop.NativeMethods.WindowBounds;

namespace ArtPet.Windows.Desktop;

public partial class PetWindow
{
    private const double WalkingSpeedDip = 58;
    private readonly DispatcherTimer _roamTimer;
    private Walk? _walk;
    private long _restUntil;

    private void RefreshRoaming()
    {
        if (Settings.FreeModeEnabled && IsVisible && _player.CanWalk)
        {
            if (!_roamTimer.IsEnabled)
            {
                PauseRoaming();
                _roamTimer.Start();
            }
        }
        else
        {
            _roamTimer.Stop();
            PauseRoaming();
        }
    }

    private void PauseRoaming()
    {
        _walk = null;
        _player.SetWalking(false);
        _restUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency * Random.Shared.Next(2, 6);
    }

    private void UpdateRoaming(object? sender, EventArgs e)
    {
        if (!SystemParameters.ClientAreaAnimation || _press is not null || IsMouseOver ||
            ContextMenu.IsOpen || _player.IsReacting || _voice.IsPlaying)
        {
            if (_walk is not null)
            {
                PauseRoaming();
            }

            return;
        }

        long now = Stopwatch.GetTimestamp();
        if ((now < _restUntil && _walk is null) || !NativeMethods.GetWindowRect(_handle, out WindowBounds bounds))
        {
            return;
        }

        if (_walk is not { } walk)
        {
            BeginWalk(bounds, now);
            return;
        }

        double progress = Math.Min(1, Stopwatch.GetElapsedTime(walk.Started, now).TotalSeconds / walk.Duration);
        int x = walk.Start.X + (int)Math.Round(((double)walk.End.X - walk.Start.X) * progress);
        int y = walk.Start.Y + (int)Math.Round(((double)walk.End.Y - walk.Start.Y) * progress);
        if (x != bounds.Left || y != bounds.Top)
        {
            if (!NativeMethods.MoveWindow(_handle, x, y, deferErase: true))
            {
                PauseRoaming();
                return;
            }

            _atPrimaryCorner = false;
        }

        if (progress == 1)
        {
            PauseRoaming();
        }
    }

    private void BeginWalk(WindowBounds bounds, long now)
    {
        var screen = Screen.FromHandle(_handle);
        var work = NativeMethods.GetWorkingArea(screen.Bounds.Location) ?? screen.WorkingArea;
        int right = Math.Max(work.Left, work.Right - bounds.Width);
        int bottom = Math.Max(work.Top, work.Bottom - bounds.Height);
        double speed = WalkingSpeedDip * Settings.SizePercent / PetSettings.DefaultSize *
            NativeMethods.GetDpiForWindow(_handle) / 96.0;
        int distance = (int)Math.Round(speed * (2 + Random.Shared.NextDouble() * 2));
        int direction = Random.Shared.Next(2) == 0 ? -1 : 1;
        int startX = Math.Clamp(bounds.Left, work.Left, right);
        int startY = Math.Clamp(bounds.Top, work.Top, bottom);
        int endX = Math.Clamp(startX + direction * distance, work.Left, right);
        if (Math.Abs(endX - startX) < distance / 2)
        {
            endX = Math.Clamp(startX - direction * distance, work.Left, right);
        }

        int endY = Math.Clamp(startY + (int)Math.Round((Random.Shared.NextDouble() - 0.5) * distance), work.Top, bottom);
        double dx = (double)endX - bounds.Left;
        double dy = (double)endY - bounds.Top;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < speed / 3)
        {
            PauseRoaming();
            return;
        }

        if (dx != 0)
        {
            FacingTransform.ScaleX = (dx < 0) != _player.MoveFacesLeft ? -1 : 1;
        }

        _walk = new Walk(new Point(bounds.Left, bounds.Top), new Point(endX, endY), now, length / speed);
        _player.SetWalking(true);
    }

    private sealed record Walk(Point Start, Point End, long Started, double Duration);
}
