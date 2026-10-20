using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PortablePlayer;

/// <summary>
/// Circular vinyl record with the album artwork masked into the label.
/// Rotation is a storyboard on a cached (GPU-composited) RenderTransform at 30 fps:
/// pausing freezes the exact angle, resuming continues from it, no per-frame code runs.
/// </summary>
public sealed class VinylView : Viewbox
{
    readonly Grid _disc = new() { Width = 200, Height = 200, RenderTransformOrigin = new Point(0.5, 0.5) };
    readonly RotateTransform _rot = new();
    readonly Ellipse _labelBottom = new() { Width = 124, Height = 124 };
    readonly Ellipse _labelTop = new() { Width = 124, Height = 124, Opacity = 0 };
    readonly TextBlock _note = new()
    {
        Text = "♪", FontSize = 46, Foreground = new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255)),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
    };

    Storyboard _sb;
    bool _started, _paused;
    double _speed = 1;
    DispatcherTimer _ramp;
    Brush _curBrush;

    static readonly Brush Placeholder = MakePlaceholder();

    static Brush MakePlaceholder()
    {
        var b = new RadialGradientBrush();
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0x4A, 0x4F, 0x5A), 0));
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0x2C, 0x30, 0x38), 1));
        b.Freeze();
        return b;
    }

    public VinylView()
    {
        Stretch = Stretch.Uniform;
        Child = _disc;
        _disc.RenderTransform = _rot;
        _disc.CacheMode = new BitmapCache { RenderAtScale = 2 };

        var body = new RadialGradientBrush();
        body.GradientStops.Add(new GradientStop(Color.FromRgb(0x2A, 0x2A, 0x2E), 0));
        body.GradientStops.Add(new GradientStop(Color.FromRgb(0x0C, 0x0C, 0x0E), 0.55));
        body.GradientStops.Add(new GradientStop(Color.FromRgb(0x1B, 0x1B, 0x1F), 1));
        _disc.Children.Add(new Ellipse { Fill = body });

        var groove = new SolidColorBrush(Color.FromArgb(0x18, 255, 255, 255));
        for (int r = 68; r <= 96; r += 4)
            _disc.Children.Add(new Ellipse { Width = r * 2, Height = r * 2, Stroke = groove, StrokeThickness = 0.6 });

        // two faint sheen wedges make the rotation visible
        var sheen = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255));
        _disc.Children.Add(new Path { Data = Geometry.Parse("M100,100 L100,4 A96,96 0 0 1 168,32 Z"), Fill = sheen, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });
        _disc.Children.Add(new Path { Data = Geometry.Parse("M100,100 L100,196 A96,96 0 0 1 32,168 Z"), Fill = sheen, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });

        _labelBottom.Fill = Placeholder;
        _curBrush = Placeholder;
        _disc.Children.Add(_labelBottom);
        _disc.Children.Add(_labelTop);
        _disc.Children.Add(new Ellipse
        {
            Width = 124, Height = 124, Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), StrokeThickness = 1.5, IsHitTestVisible = false
        });
        _disc.Children.Add(_note);
        _disc.Children.Add(new Ellipse
        {
            Width = 13, Height = 13, Fill = new SolidColorBrush(Color.FromRgb(0x0C, 0x0C, 0x0E)),
            Stroke = new SolidColorBrush(Color.FromArgb(0x60, 255, 255, 255)), StrokeThickness = 1
        });
    }

    /// <summary>Replace the label artwork; fadeMs = 0 switches instantly.</summary>
    public void SetArtwork(BitmapSource art, int fadeMs)
    {
        Brush nb = art != null ? new ImageBrush(art) { Stretch = Stretch.UniformToFill } : Placeholder;
        _note.Visibility = art != null ? Visibility.Collapsed : Visibility.Visible;

        _labelTop.BeginAnimation(OpacityProperty, null);
        _labelTop.Opacity = 0;
        _labelBottom.Fill = _curBrush;            // finish any transition that was still running
        _curBrush = nb;

        if (fadeMs <= 0) { _labelBottom.Fill = nb; return; }

        _labelTop.Fill = nb;
        var a = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(fadeMs)) { EasingFunction = new QuadraticEase() };
        a.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_curBrush, nb)) return;
            _labelBottom.Fill = nb;
            _labelTop.BeginAnimation(OpacityProperty, null);
            _labelTop.Opacity = 0;
        };
        _labelTop.BeginAnimation(OpacityProperty, a);
    }

    void EnsureStoryboard()
    {
        if (_sb != null) return;
        var a = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(8)) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(a, 30);      // plenty for a slow record, halves the compositing work
        Storyboard.SetTarget(a, _disc);
        Storyboard.SetTargetProperty(a, new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));
        _sb = new Storyboard();
        _sb.Children.Add(a);
    }

    /// <summary>Drive the vinyl from the playback state. Rotation only runs in Full animation mode.</summary>
    public void Apply(PlayState st, AnimMode mode)
    {
        bool spin = st == PlayState.Playing && mode == AnimMode.Full;
        if (st == PlayState.Loading && _started && !_paused) return;       // keep spinning across track changes

        if (spin)
        {
            EnsureStoryboard();
            if (!_started)
            {
                _speed = 0.05;
                _sb.Begin(this, true);
                _sb.SetSpeedRatio(this, _speed);
                _started = true; _paused = false;
            }
            else if (_paused) { _sb.Resume(this); _paused = false; }
            RampTo(1, null);
        }
        else if (st == PlayState.Paused || st == PlayState.Loading || st == PlayState.Playing)
            Halt(false, st == PlayState.Paused && mode == AnimMode.Full);   // freeze at the current angle
        else
            Halt(true, false);                                              // stopped / no media / error: back to idle
    }

    void Halt(bool reset, bool smooth)
    {
        if (!_started) return;
        _ramp?.Stop();
        void Finish()
        {
            if (reset) { _sb.Stop(this); _started = false; _paused = false; _rot.Angle = 0; }
            else { _sb.Pause(this); _paused = true; }
        }
        if (smooth && !_paused) RampTo(0, Finish);
        else if (!_paused || reset) Finish();
    }

    /// <summary>Stops rendering work while the panel is hidden; Apply() resumes from the same angle.</summary>
    public void Suspend()
    {
        if (!_started || _paused) return;
        _ramp?.Stop();
        _sb.Pause(this);
        _paused = true;
    }

    // short ease-in / ease-out of the rotation speed (only runs for ~450 ms on play/pause)
    void RampTo(double target, Action done)
    {
        _ramp?.Stop();
        if (_sb == null) return;
        double from = _speed;
        var start = DateTime.UtcNow;
        _ramp = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _ramp.Tick += (_, _) =>
        {
            double t = Math.Min(1, (DateTime.UtcNow - start).TotalMilliseconds / 450);
            _speed = from + (target - from) * t * t * (3 - 2 * t);
            _sb.SetSpeedRatio(this, Math.Max(0.02, _speed));
            if (t >= 1) { _ramp.Stop(); done?.Invoke(); }
        };
        _ramp.Start();
    }
}
