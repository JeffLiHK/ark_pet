using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using ArtPet.Windows.Media;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;
using WindowBounds = ArtPet.Windows.Desktop.NativeMethods.WindowBounds;

namespace ArtPet.Windows.Desktop;

public partial class PetWindow
{
    private const double WalkingSpeedDip = 32;
    private const int WalkWeight = 60;
    private const int IdleWeight = 24;
    private const int SitWeight = 10;
    private const int SleepWeight = 6;
    private bool _roaming;
    private (TimeSpan RenderingTime, long Timestamp)? _lastRoamingFrame;
    private Walk? _walk;
    private long _restUntil;

    private bool CanSitHere =>
        _player.CanSit && (!Settings.PhysicsModeEnabled || _support?.Kind == DesktopSurfaceKind.Window);

    private bool CanSleepHere => _player.CanSleep && (!Settings.PhysicsModeEnabled ||
        _support?.Kind is DesktopSurfaceKind.Taskbar or DesktopSurfaceKind.ScreenBottom);

    private bool CanChooseRoamingAction =>
        WalkEnabled && (!Settings.PhysicsModeEnabled || _support is not null) &&
        (_player.CanWalk || CanSitHere || CanSleepHere);

    private void RefreshRoaming()
    {
        if (IsVisible && (Settings.PhysicsModeEnabled || CanChooseRoamingAction))
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
            ResetPhysicsPosition();
        }
    }

    private void PauseRoaming() => Rest(GetPhysicsAction(), Stopwatch.GetTimestamp(), 1.5, 4);

    private void Rest(RoamingAction action, long now, double minimumSeconds, double maximumSeconds)
    {
        _walk = null;
        _player.SetRoamingAction(action);
        double seconds = minimumSeconds + Random.Shared.NextDouble() * (maximumSeconds - minimumSeconds);
        _restUntil = now + (long)(Stopwatch.Frequency * seconds);
    }

    private void ChooseRoamingAction(long now)
    {
        int walk = _player.CanWalk && IsWithinActivityArea ? WalkWeight : 0;
        int sit = CanSitHere ? SitWeight : 0;
        int sleep = CanSleepHere ? SleepWeight : 0;
        int choice = Random.Shared.Next(walk + IdleWeight + sit + sleep);
        if (choice < walk)
        {
            BeginWalk(Random.Shared.Next(2) == 0 ? -1 : 1);
        }
        else if (choice < walk + IdleWeight)
        {
            Rest(RoamingAction.Idle, now, 1.5, 6);
        }
        else if (choice < walk + IdleWeight + sit)
        {
            Rest(RoamingAction.Sit, now, 3, 9);
        }
        else
        {
            Rest(RoamingAction.Sleep, now, 6, 14);
        }
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

        if (Settings.PhysicsModeEnabled && _press is null && !ContextMenu.IsOpen && !_voice.IsPlaying)
        {
            DesktopSurface? previousSupport = _support;
            UpdatePhysics(elapsed, now);
            if (previousSupport?.Handle != _support?.Handle || previousSupport?.Kind != _support?.Kind)
            {
                PauseRoaming();
            }
            else if (!CanChooseRoamingAction)
            {
                _player.SetRoamingAction(GetPhysicsAction());
            }
        }

        if (!CanChooseRoamingAction || (_pairReaction && _player.IsReacting))
        {
            return;
        }
        _pairReaction = false;

        if (!SystemParameters.ClientAreaAnimation || _press is not null || IsMouseOver ||
            ContextMenu.IsOpen || _player.IsReacting || _voice.IsPlaying)
        {
            if (_walk is not null || (!Settings.PhysicsModeEnabled && _player.IsRestingPose))
            {
                PauseRoaming();
            }

            return;
        }

        if (_walk is not { } walk)
        {
            if (now >= _restUntil)
            {
                ChooseRoamingAction(now);
            }

            return;
        }

        if (Settings.PhysicsModeEnabled && (!NativeMethods.GetWindowRect(_handle, out WindowBounds bounds) ||
            !ContainsFeet(walk.Area, GetFeet(bounds))))
        {
            PauseRoaming();
            return;
        }

        walk.Distance += elapsed * GetWalkingSpeed(walk.DpiScale) * walk.SpeedMultiplier;
        double progress = Math.Min(1, walk.Distance / walk.Length);
        int top = walk.Start.Y + (int)Math.Round(((double)walk.End.Y - walk.Start.Y) * progress);
        if (Settings.PhysicsModeEnabled)
        {
            if (_lastPhysicsTop is not { } physicsTop)
            {
                PauseRoaming();
                return;
            }

            top = physicsTop;
        }

        var position = new Point(
            walk.Start.X + (int)Math.Round(((double)walk.End.X - walk.Start.X) * progress), top);
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
            ChooseRoamingAction(now);
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

        var work = GetActivityArea(out _);
        var feet = GetFeet(bounds);
        if (!ContainsFeet(work, feet))
        {
            PauseRoaming();
            return;
        }

        double dpiScale = NativeMethods.GetDpiForWindow(_handle) / 96.0;
        double speedMultiplier = 0.85 + Random.Shared.NextDouble() * 0.3;
        double speed = GetWalkingSpeed(dpiScale) * speedMultiplier;
        int distance = (int)Math.Round(speed * (2 + Random.Shared.NextDouble() * 5));
        double requestedX = Math.Clamp(feet.X + direction * distance, work.Left, work.Right);
        if (Math.Abs(requestedX - feet.X) < 1)
        {
            requestedX = Math.Clamp(feet.X - direction * distance, work.Left, work.Right);
        }

        FaceDirection(requestedX - feet.X);
        if (!NativeMethods.GetWindowRect(_handle, out bounds))
        {
            PauseRoaming();
            return;
        }

        feet = GetFeet(bounds);
        double targetY = Math.Clamp(feet.Y + (Random.Shared.NextDouble() - 0.5) * distance, work.Top, work.Bottom);
        int dx = (int)Math.Truncate(requestedX - feet.X);
        int dy = Settings.PhysicsModeEnabled ? 0 : (int)Math.Truncate(targetY - feet.Y);
        double length = Math.Sqrt((double)dx * dx + (double)dy * dy);
        if (speed <= 0 || length == 0)
        {
            PauseRoaming();
            return;
        }

        _walk = new Walk(new Point(bounds.Left, bounds.Top), new Point(bounds.Left + dx, bounds.Top + dy),
            length, dpiScale, speedMultiplier, work);
        _player.SetRoamingAction(RoamingAction.Walk);
    }

    private sealed record Walk(Point Start, Point End, double Length, double DpiScale, double SpeedMultiplier, Rectangle Area)
    {
        internal double Distance { get; set; }
        internal Point LastPosition { get; set; } = Start;
    }
}
