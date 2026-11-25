using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ArtPet.Windows.Media;

public partial class SproutVisual : UserControl
{
    internal const double IdleCycleMs = 3100;
    internal const double ReactionMs = 620;
    private readonly Stopwatch _clock = new();
    private bool _reacting;
    private long _lastFrame;

    public SproutVisual()
    {
        InitializeComponent();
    }

    internal int FramesPerSecond { get; set; } = PetSettings.DefaultAnimationFps;

    internal void Start()
    {
        if (_clock.IsRunning)
        {
            return;
        }

        _reacting = false;
        RestartAnimation();
        CompositionTarget.Rendering += UpdateFrame;
    }

    internal void Stop()
    {
        if (_clock.IsRunning)
        {
            CompositionTarget.Rendering -= UpdateFrame;
            _clock.Reset();
        }
    }

    internal void React()
    {
        if (_reacting || !_clock.IsRunning)
        {
            return;
        }

        _reacting = true;
        RestartAnimation();
    }

    internal void CancelReaction()
    {
        if (_reacting && _clock.IsRunning)
        {
            _reacting = false;
            RestartAnimation();
        }
    }

    private void RestartAnimation()
    {
        _clock.Restart();
        _lastFrame = 0;
        DrawFrame();
    }

    private void UpdateFrame(object? sender, EventArgs e)
    {
        long frame = (long)(_clock.Elapsed.TotalSeconds * FramesPerSecond);
        if (frame != _lastFrame)
        {
            _lastFrame = frame;
            DrawFrame();
        }
    }

    private void DrawFrame()
    {
        double elapsed = _clock.Elapsed.TotalMilliseconds;
        if (_reacting && elapsed >= ReactionMs)
        {
            _reacting = false;
            _clock.Restart();
            _lastFrame = 0;
            elapsed = 0;
        }

        DrawPose(elapsed, _reacting);
    }

    internal void DrawPose(double elapsed, bool reacting)
    {
        Smile.Visibility = reacting ? Visibility.Collapsed : Visibility.Visible;
        HappySmile.Visibility = reacting ? Visibility.Visible : Visibility.Collapsed;
        if (reacting)
        {
            double progress = elapsed / ReactionMs;
            double lift = Math.Sin(Math.PI * progress);
            Hop.Y = -24 * lift;
            Breath.ScaleX = 1 + 0.025 * lift;
            Breath.ScaleY = 1 - 0.035 * lift;
            Blink.ScaleY = 1;
            Sparkles.Opacity = 1 - progress;
            return;
        }

        double phase = elapsed % IdleCycleMs;
        double breathing = (1 - Math.Cos(2 * Math.PI * phase / IdleCycleMs)) / 2;
        Hop.Y = 0;
        Breath.ScaleX = 1 + 0.018 * breathing;
        Breath.ScaleY = 1 - 0.045 * breathing;
        Blink.ScaleY = phase is >= 2150 and < 2300 ? 0.18 : 1;
        Sparkles.Opacity = 0;
    }
}
