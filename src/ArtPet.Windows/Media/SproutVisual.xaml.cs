using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ArtPet.Windows.Media;

public partial class SproutVisual : UserControl
{
    private const double IdleCycleMs = 3100;
    private const double ReactionMs = 620;
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

        SetReaction(false);
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

        SetReaction(true);
        RestartAnimation();
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
            SetReaction(false);
            _clock.Restart();
            _lastFrame = 0;
            elapsed = 0;
        }

        if (_reacting)
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

    private void SetReaction(bool reacting)
    {
        _reacting = reacting;
        Smile.Visibility = reacting ? Visibility.Collapsed : Visibility.Visible;
        HappySmile.Visibility = reacting ? Visibility.Visible : Visibility.Collapsed;
    }
}
