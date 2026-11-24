using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ArtPet.Windows.Media;

internal sealed class FramePlayer
{
    private readonly Image _image;
    private readonly FrameworkElement _breeze;
    private LoadedOutfit? _outfit;
    private FrameClip? _clip;
    private long _started;
    private long? _breezeStarted;
    private bool _playingTouch;
    private int _index;
    private int _idleClipIndex;

    internal FramePlayer(Image image, FrameworkElement breeze)
    {
        _image = image;
        _breeze = breeze;
    }

    internal bool HasOutfit => _outfit is not null;
    internal int FramesPerSecond { get; set; } = PetSettings.DefaultAnimationFps;

    internal void SetOutfit(LoadedOutfit outfit, bool visible)
    {
        Stop();
        _outfit = outfit;
        _image.Source = outfit.Idle.Frames[0];
        if (visible)
        {
            Start();
        }
    }

    internal void Start()
    {
        if (_clip is null && _outfit is { } outfit)
        {
            _idleClipIndex = 0;
            BeginClip(outfit.Idle, playingTouch: false);
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
        _playingTouch = false;
        _breezeStarted = null;
        _breeze.Opacity = 0;
    }

    internal void React()
    {
        if (_clip is null || _outfit is null || _playingTouch || _breezeStarted is not null)
        {
            return;
        }

        if (_outfit.Touch is { } touch)
        {
            BeginClip(touch, playingTouch: true);
        }
        else
        {
            _breezeStarted = Stopwatch.GetTimestamp();
            _breeze.Opacity = 1;
        }
    }

    private void BeginClip(FrameClip clip, bool playingTouch)
    {
        _clip = clip;
        _index = 0;
        _playingTouch = playingTouch;
        _started = Stopwatch.GetTimestamp();
        _image.Source = clip.Frames[0];
    }

    private void BeginNextIdleClip(LoadedOutfit outfit)
    {
        int count = outfit.IdleClips.Count;
        if (count > 1)
        {
            // Pick uniformly from the other idle clips to avoid consecutive repeats.
            _idleClipIndex = (_idleClipIndex + Random.Shared.Next(1, count)) % count;
        }

        BeginClip(outfit.IdleClips[_idleClipIndex], playingTouch: false);
    }

    private void UpdateFrame(object? sender, EventArgs e)
    {
        if (_clip is not { } clip || _outfit is not { } outfit)
        {
            return;
        }

        if (_breezeStarted is { } start)
        {
            _breeze.Opacity = Math.Max(0, 1 - Stopwatch.GetElapsedTime(start).TotalMilliseconds / 500);
            if (_breeze.Opacity == 0)
            {
                _breezeStarted = null;
            }
        }

        double elapsed = Stopwatch.GetElapsedTime(_started).TotalSeconds;
        if (elapsed >= (double)clip.Frames.Length / clip.Fps && (_playingTouch || outfit.IdleClips.Count > 1))
        {
            BeginNextIdleClip(outfit);
            return;
        }

        int playbackRate = Math.Min(FramesPerSecond, clip.Fps);
        long displayedFrame = (long)(elapsed * playbackRate);
        long sourceFrame = displayedFrame * clip.Fps / playbackRate;
        int index = (int)(sourceFrame % clip.Frames.Length);
        if (index != _index)
        {
            _index = index;
            _image.Source = clip.Frames[index];
        }
    }
}
