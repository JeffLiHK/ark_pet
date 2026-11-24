using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ArtPet.Windows.Media;

internal sealed class FramePlayer
{
    private readonly Image _image;
    private readonly FrameworkElement _breeze;
    private readonly DispatcherTimer _timer;
    private LoadedOutfit? _outfit;
    private FrameClip? _clip;
    private long _started;
    private long? _breezeStarted;
    private bool _playingTouch;
    private int _index;

    internal FramePlayer(Image image, FrameworkElement breeze)
    {
        _image = image;
        _breeze = breeze;
        _timer = new DispatcherTimer(DispatcherPriority.Background);
        _timer.Tick += (_, _) => UpdateFrame();
    }

    internal bool HasOutfit => _outfit is not null;

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
        if (_outfit is { } outfit)
        {
            BeginClip(outfit.Idle, playingTouch: false);
            _timer.Start();
        }
    }

    internal void Stop()
    {
        _timer.Stop();
        _clip = null;
        _playingTouch = false;
        _breezeStarted = null;
        _breeze.Opacity = 0;
    }

    internal void React()
    {
        if (!_timer.IsEnabled || _outfit is null || _playingTouch || _breezeStarted is not null)
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
        _timer.Interval = TimeSpan.FromSeconds(1d / clip.Fps);
        _image.Source = clip.Frames[0];
    }

    private void UpdateFrame()
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

        long position = (long)(Stopwatch.GetElapsedTime(_started).TotalSeconds * clip.Fps);
        if (_playingTouch && position >= clip.Frames.Length)
        {
            BeginClip(outfit.Idle, playingTouch: false);
            return;
        }

        int index = (int)(position % clip.Frames.Length);
        if (index != _index)
        {
            _index = index;
            _image.Source = clip.Frames[index];
        }
    }
}
