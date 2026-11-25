using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ArtPet.Windows.Media;

internal enum RoamingAction
{
    Idle,
    Walk,
    Sleep,
    Sit
}

internal sealed class FramePlayer
{
    private const double ActionTransitionSeconds = 0.16;
    private const double TouchTransitionSeconds = 0.10;
    private readonly Image _image;
    private readonly FrameworkElement _breeze;
    private LoadedOutfit? _outfit;
    private FrameClip? _clip;
    private FrameTransition? _transition;
    private long _started;
    private long? _breezeStarted;
    private bool _playingTouch;
    private RoamingAction _roamingAction;
    private bool _freeMode;
    private bool _physicsMode;
    private int _index;
    private int _idleClipIndex;

    internal FramePlayer(Image image, FrameworkElement breeze)
    {
        _image = image;
        _breeze = breeze;
    }

    internal bool HasOutfit => _outfit is not null;
    internal bool CanWalk => _outfit?.Move is not null;
    internal bool CanSleep => _outfit?.Sleep is not null;
    internal bool IsSleeping => _roamingAction == RoamingAction.Sleep;
    internal bool IsReacting => _playingTouch || _breezeStarted is not null;
    internal bool MoveFacesLeft => _outfit?.Definition.MoveFacesLeft == true;
    internal int FramesPerSecond { get; set; } = PetSettings.DefaultAnimationFps;
    internal int IdleLoopsPerAction { get; set; } = PetSettings.DefaultIdleLoopsPerAction;

    internal void SetMovementModes(bool freeMode, bool physicsMode)
    {
        _freeMode = freeMode;
        _physicsMode = physicsMode;
        if ((physicsMode || freeMode && CanWalk) && _outfit is { } outfit && _idleClipIndex != 0)
        {
            _idleClipIndex = 0;
            if (!_playingTouch && _clip is not null)
            {
                BeginClip(outfit.Idle, playingTouch: false, Stopwatch.GetTimestamp());
            }
        }
    }

    internal void SetRoamingAction(RoamingAction action)
    {
        if (_roamingAction == action || _clip is null || _outfit is not { } outfit)
        {
            return;
        }

        _roamingAction = action;
        _idleClipIndex = 0;
        FrameClip clip = GetRestingClip(outfit);
        if (!_playingTouch && !ReferenceEquals(_clip, clip))
        {
            BeginClip(clip, playingTouch: false, Stopwatch.GetTimestamp());
        }
    }

    private FrameClip GetRestingClip(LoadedOutfit outfit) => _roamingAction switch
    {
        RoamingAction.Walk => outfit.Move ?? outfit.Idle,
        RoamingAction.Sleep => outfit.Sleep ?? outfit.Sit ?? outfit.Idle,
        RoamingAction.Sit => outfit.Sit ?? outfit.Idle,
        _ => outfit.IdleClips[_idleClipIndex]
    };

    internal void CancelReaction()
    {
        _breezeStarted = null;
        _breeze.Opacity = 0;
        if (_playingTouch)
        {
            Stop();
            Start();
        }
    }

    internal void SetOutfit(LoadedOutfit outfit, bool visible)
    {
        Stop();
        _outfit = outfit;
        _idleClipIndex = 0;
        if (visible)
        {
            Start();
        }
        else
        {
            _image.Source = outfit.Idle.Frames[0];
        }
    }

    internal void Start()
    {
        if (_clip is null && _outfit is { } outfit)
        {
            _idleClipIndex = 0;
            BeginClip(outfit.Idle, playingTouch: false, Stopwatch.GetTimestamp());
            CompositionTarget.Rendering += UpdateFrame;
        }
    }

    internal void Stop()
    {
        if (_clip is not null)
        {
            CompositionTarget.Rendering -= UpdateFrame;
        }

        _clip = null;
        _transition = null;
        _playingTouch = false;
        _roamingAction = RoamingAction.Idle;
        _breezeStarted = null;
        _breeze.Opacity = 0;
        _image.Source = _outfit?.Idle.Frames[0];
    }

    internal void React()
    {
        if (_clip is null || _outfit is null || _playingTouch || _breezeStarted is not null)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (_outfit.Touch is { } touch)
        {
            BeginClip(touch, playingTouch: true, now);
        }
        else
        {
            _breezeStarted = now;
            _breeze.Opacity = 1;
        }
    }

    private void BeginClip(FrameClip clip, bool playingTouch, long now)
    {
        if (_clip is not null && _image.Source is BitmapSource outgoing)
        {
            double duration = _playingTouch || playingTouch ? TouchTransitionSeconds : ActionTransitionSeconds;
            // Leave time for even very short clips to reach full opacity.
            duration = Math.Min(duration, (double)clip.Frames.Length / clip.Fps / 3);
            (_transition ??= new FrameTransition(outgoing)).Begin(outgoing, now, duration);
        }
        else
        {
            _image.Source = clip.Frames[0];
        }

        _clip = clip;
        _index = 0;
        _playingTouch = playingTouch;
        _started = now;
    }

    private void BeginNextIdleClip(LoadedOutfit outfit, long now)
    {
        var candidates = Enumerable.Range(0, outfit.IdleClips.Count)
            .Where(index => index != _idleClipIndex)
            .ToArray();
        if (candidates.Length > 0)
        {
            _idleClipIndex = candidates[Random.Shared.Next(candidates.Length)];
            BeginClip(outfit.IdleClips[_idleClipIndex], playingTouch: false, now);
        }
    }

    private void UpdateFrame(object? sender, EventArgs e)
    {
        if (_clip is not { } clip || _outfit is not { } outfit)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (_breezeStarted is { } start)
        {
            _breeze.Opacity = Math.Max(0, 1 - Stopwatch.GetElapsedTime(start, now).TotalMilliseconds / 500);
            if (_breeze.Opacity == 0)
            {
                _breezeStarted = null;
            }
        }

        int frameCount = clip.Frames.Length;
        double elapsedSourceFrames = Stopwatch.GetElapsedTime(_started, now).TotalSeconds * clip.Fps;
        if (_playingTouch && elapsedSourceFrames >= frameCount)
        {
            BeginClip(GetRestingClip(outfit), playingTouch: false, now);
            return;
        }

        if (!_playingTouch && !_physicsMode && !(_freeMode && CanWalk) && outfit.IdleClips.Count > 1 &&
            elapsedSourceFrames >= (long)frameCount * IdleLoopsPerAction)
        {
            BeginNextIdleClip(outfit, now);
            return;
        }

        int playbackRate = Math.Min(FramesPerSecond, clip.Fps);
        // Sample each loop from its first frame, including clips with an odd frame count at half rate.
        double sourceFramesInLoop = elapsedSourceFrames % frameCount;
        int displayedFrame = (int)(sourceFramesInLoop * playbackRate / clip.Fps);
        int index = displayedFrame * clip.Fps / playbackRate;
        if (_transition is { IsActive: true } transition)
        {
            _index = index;
            _image.Source = transition.Blend(clip.Frames[index], now);
        }
        else if (index != _index)
        {
            _index = index;
            _image.Source = clip.Frames[index];
        }
    }
}
