using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using ArtPet.Windows.Media;
using Point = System.Drawing.Point;
using Screen = System.Windows.Forms.Screen;
using WindowBounds = ArtPet.Windows.Desktop.NativeMethods.WindowBounds;

namespace ArtPet.Windows.Desktop;

public partial class PetWindow
{
    private const double WalkingSpeedDip = 32;
    private bool _roaming;
    private (TimeSpan RenderingTime, long Timestamp)? _lastRoamingFrame;
    private Walk? _walk;
    private long _restUntil;

    private void RefreshRoaming()
    {
        if (Settings.FreeModeEnabled && IsVisible && _player.CanWalk)
        {
            if (!_roaming)
            {
                PauseRoaming();
                CompositionTarget.Rendering += UpdateRoaming;
                _roaming = true;
            }
        }
        else
        {
            StopRoaming();
            PauseRoaming();
        }
    }

    private void StopRoaming()
    {
        if (_roaming)
        {
            CompositionTarget.Rendering -= UpdateRoaming;
            _roaming = false;
            _lastRoamingFrame = null;
        }
    }

    private void PauseRoaming(bool allowSleep = false)
    {
        _walk = null;
        bool sleep = allowSleep && _player.CanSleep && Random.Shared.Next(4) == 0;
        _player.SetRoamingAction(sleep ? RoamingAction.Sleep : RoamingAction.Idle);
        int seconds = sleep ? Random.Shared.Next(8, 17) : Random.Shared.Next(2, 6);
        _restUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency * seconds;
    }

    private void UpdateRoaming(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs frame || frame.RenderingTime == _lastRoamingFrame?.RenderingTime)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        // Sample once per render pass and avoid large jumps after a stalled frame.
        double elapsed = _lastRoamingFrame is { } previous
            ? Math.Min(Stopwatch.GetElapsedTime(previous.Timestamp, now).TotalSeconds, 0.05)
            : 0;
        _lastRoamingFrame = (frame.RenderingTime, now);

        if (!SystemParameters.ClientAreaAnimation || _press is not null || IsMouseOver ||
            ContextMenu.IsOpen || _player.IsReacting || _voice.IsPlaying)
        {
            if (_walk is not null || _player.IsSleeping)
            {
                PauseRoaming();
            }

            return;
        }

        if (_walk is not { } walk)
        {
            if (now >= _restUntil)
            {
                BeginWalk(Random.Shared.Next(2) == 0 ? -1 : 1);
            }

            return;
        }

        walk.Distance += elapsed * GetWalkingSpeed(walk.DpiScale);
        double progress = Math.Min(1, walk.Distance / walk.Length);
        var position = new Point(
            walk.Start.X + (int)Math.Round(((double)walk.End.X - walk.Start.X) * progress),
            walk.Start.Y + (int)Math.Round(((double)walk.End.Y - walk.Start.Y) * progress));
        if (position != walk.LastPosition)
        {
            if (!NativeMethods.MoveWindow(_handle, position.X, position.Y, deferErase: true))
            {
                PauseRoaming();
                return;
            }

            walk.LastPosition = position;
            _atPrimaryCorner = false;
        }

        if (progress == 1)
        {
            if (walk.ReturnDirection != 0 && Random.Shared.Next(3) != 0)
            {
                BeginWalk(walk.ReturnDirection);
            }
            else
            {
                PauseRoaming(allowSleep: true);
            }
        }
    }

    private double GetWalkingSpeed(double dpiScale) => WalkingSpeedDip * dpiScale *
        Settings.SizePercent / PetSettings.DefaultSize * Settings.WalkingSpeedPercent / PetSettings.DefaultWalkingSpeedPercent;

    private void BeginWalk(int direction)
    {
        if (!NativeMethods.GetWindowRect(_handle, out WindowBounds bounds))
        {
            PauseRoaming();
            return;
        }

        var screen = Screen.FromHandle(_handle);
        var work = NativeMethods.GetWorkingArea(screen.Bounds.Location) ?? screen.WorkingArea;
        int right = Math.Max(work.Left, work.Right - bounds.Width);
        int bottom = Math.Max(work.Top, work.Bottom - bounds.Height);
        double dpiScale = NativeMethods.GetDpiForWindow(_handle) / 96.0;
        double speed = GetWalkingSpeed(dpiScale);
        int distance = (int)Math.Round(speed * (3 + Random.Shared.NextDouble() * 3));
        int startX = Math.Clamp(bounds.Left, work.Left, right);
        int startY = Math.Clamp(bounds.Top, work.Top, bottom);
        int endX = Math.Clamp(startX + direction * distance, work.Left, right);
        if (endX == startX)
        {
            endX = Math.Clamp(startX - direction * distance, work.Left, right);
        }

        int endY = Math.Clamp(startY + (int)Math.Round((Random.Shared.NextDouble() - 0.5) * distance), work.Top, bottom);
        double dx = (double)endX - bounds.Left;
        double dy = (double)endY - bounds.Top;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (speed <= 0 || length == 0)
        {
            PauseRoaming();
            return;
        }

        if (dx != 0)
        {
            FacingTransform.ScaleX = (dx < 0) != _player.MoveFacesLeft ? -1 : 1;
        }

        int returnDirection = dx > 0 && endX == right ? -1 : dx < 0 && endX == work.Left ? 1 : 0;
        _walk = new Walk(new Point(bounds.Left, bounds.Top), new Point(endX, endY), length, dpiScale, returnDirection);
        _player.SetRoamingAction(RoamingAction.Walk);
    }

    private sealed record Walk(Point Start, Point End, double Length, double DpiScale, int ReturnDirection)
    {
        internal double Distance { get; set; }
        internal Point LastPosition { get; set; } = Start;
    }
}
