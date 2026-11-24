using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ArtPet.Windows.Media;

public partial class SproutVisual : UserControl
{
    private const double IdleCycleMs = 3100;
    private const double ReactionMs = 620;
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(50);
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _timer;
    private bool _reacting;

    public SproutVisual()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = FrameInterval };
        _timer.Tick += (_, _) => DrawFrame();
    }

    internal void Start()
    {
        SetReaction(false);
        _clock.Restart();
        DrawFrame();
        _timer.Start();
    }

    internal void Stop()
    {
        _timer.Stop();
        _clock.Reset();
    }

    internal void React()
    {
        if (_reacting || !_timer.IsEnabled)
        {
            return;
        }

        SetReaction(true);
        _clock.Restart();
        DrawFrame();
    }

    private void DrawFrame()
    {
        double elapsed = _clock.Elapsed.TotalMilliseconds;
        if (_reacting && elapsed >= ReactionMs)
        {
            SetReaction(false);
            _clock.Restart();
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
