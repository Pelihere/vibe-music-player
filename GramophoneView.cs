using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace PortablePlayer;

/// <summary>
/// The Gramophone: a floating spinning record with a brass tonearm and needle, plus the mechanical record changer.
/// It has its OWN fixed visual identity (literal colours only) - Main Window themes never touch it.
/// It drives itself from PlaybackCore while visible, so the main window and the floating panel just host it.
///
///  * Needle position = currentTime / duration (smoothly eased, only large jumps are animated).
///  * Play: record spins up, arm swings in and the needle lowers. Pause: needle lifts. Stop: arm parks.
///  * Track change: needle lifts, record slows, arm swings away, old record leaves, new record enters and settles,
///    spins up, arm swings in, needle lowers. Audio is never delayed by any of this (visual only).
/// </summary>
public sealed class GramophoneView : Viewbox
{
    // ---------- design geometry (the whole control is one 320 x 300 canvas scaled by the Viewbox) ----------
    const double W = 320, H = 300, ArmLen = 170, RecordSize = 224;
    static readonly Point Cen = new(140, 150);      // platter centre
    static readonly Point Piv = new(278, 34);       // tonearm pivot
    static readonly double PhiOut = AngleFor(104);  // arm angle with the needle in the outer groove
    static readonly double PhiIn = AngleFor(46);    // ... in the inner groove (end of the song)
    const double PhiRest = 84;                      // parked on the rest

    /// <summary>Arm angle (degrees, clockwise from +x) for a needle at distance r from the platter centre.</summary>
    static double AngleFor(double r)
    {
        double dx = Cen.X - Piv.X, dy = Cen.Y - Piv.Y;
        double d = Math.Sqrt(dx * dx + dy * dy);
        double phiPc = Math.Atan2(dy, dx);
        double cosG = (d * d + ArmLen * ArmLen - r * r) / (2 * d * ArmLen);
        double g = Math.Acos(Math.Clamp(cosG, -1, 1));
        return (phiPc - g) * 180 / Math.PI;
    }

    // ---------- animatable state ----------
    /// <summary>0 = arm over the record, 1 = parked on its rest.</summary>
    public static readonly DependencyProperty SwingProperty =
        DependencyProperty.Register(nameof(Swing), typeof(double), typeof(GramophoneView), new PropertyMetadata(1.0, OnArm));
    /// <summary>0 = needle in the groove, 1 = lifted.</summary>
    public static readonly DependencyProperty LiftProperty =
        DependencyProperty.Register(nameof(Lift), typeof(double), typeof(GramophoneView), new PropertyMetadata(1.0, OnArm));
    /// <summary>Song progress 0..1 = needle position from the outer to the inner groove.</summary>
    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(nameof(Progress), typeof(double), typeof(GramophoneView), new PropertyMetadata(0.0, OnArm));

    public double Swing { get => (double)GetValue(SwingProperty); set => SetValue(SwingProperty, value); }
    public double Lift { get => (double)GetValue(LiftProperty); set => SetValue(LiftProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    static void OnArm(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((GramophoneView)d).UpdateArm();

    // ---------- parts ----------
    sealed class Slot
    {
        public readonly VinylView V = new() { Width = RecordSize, Height = RecordSize, RenderTransformOrigin = new Point(0.5, 0.5) };
        public readonly TranslateTransform T = new();
        public readonly ScaleTransform S = new(1, 1);
        public Slot()
        {
            V.RenderTransform = new TransformGroup { Children = { S, T } };
            Canvas.SetLeft(V, Cen.X - RecordSize / 2);
            Canvas.SetTop(V, Cen.Y - RecordSize / 2);
        }
    }

    readonly Slot _a = new(), _b = new();
    Slot _front;
    readonly RotateTransform _armRot = new(), _shadowRot = new();
    readonly ScaleTransform _armScale = new(1, 1);
    readonly Canvas _arm, _shadow;

    readonly Dictionary<(IAnimatable, DependencyProperty), DoubleAnimation> _run = new();
    Track _shown;
    System.Windows.Media.Imaging.BitmapSource _artShown;
    bool _changing, _attached, _progAnimating;
    int _chgTok, _armTok;

    static App A => App.Instance;
    static PlaybackCore C => App.Instance.Core;
    static AppSettings S => App.Instance.Settings;
    static double F => S.Animations switch { AnimMode.Full => 1.0, AnimMode.Reduced => 0.5, _ => 0.0 };

    public GramophoneView()
    {
        Stretch = Stretch.Uniform;
        var root = new Canvas { Width = W, Height = H, ClipToBounds = true };
        Child = root;

        // no turntable base: the record floats, with only a soft shadow so it does not look pasted on
        var halo = Disc(0, 0, 122, new RadialGradientBrush(new GradientStopCollection
        {
            new GradientStop(Rgb("#66000000"), 0.0), new GradientStop(Rgb("#44000000"), 0.78), new GradientStop(Rgb("#00000000"), 1.0)
        }));
        halo.IsHitTestVisible = false;
        halo.CacheMode = new BitmapCache { RenderAtScale = 1.5 };
        Place(halo, Cen.X - 122, Cen.Y - 122 + 12);
        root.Children.Add(halo);

        // records (b is the incoming slot, hidden until needed)
        _front = _a;
        _b.V.Visibility = Visibility.Collapsed;
        root.Children.Add(_b.V);
        root.Children.Add(_a.V);

        // arm (shadow first, so it sits underneath)
        _shadow = BuildArm(true, _shadowRot);
        _arm = BuildArm(false, _armRot);
        _arm.RenderTransform = new TransformGroup { Children = { _armScale, _armRot } };
        _shadow.RenderTransform = new TransformGroup { Children = { new ScaleTransform(1, 1), _shadowRot } };
        Canvas.SetLeft(_arm, Piv.X); Canvas.SetTop(_arm, Piv.Y);
        root.Children.Add(_shadow);
        root.Children.Add(_arm);
        root.Children.Add(MakePivotCap());
        UpdateArm();

        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Attach(); else Detach(); };
        Unloaded += (_, _) => Detach();
    }

    // ------------------------------------------------------------------ drawing (fixed palette)
    static Color Rgb(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    static LinearGradientBrush Grad(string a, string b, double x1 = 0, double y1 = 0, double x2 = 1, double y2 = 1)
    {
        var g = new LinearGradientBrush(Rgb(a), Rgb(b), new Point(x1, y1), new Point(x2, y2));
        g.Freeze();
        return g;
    }

    static Brush Solid(string hex) { var b = new SolidColorBrush(Rgb(hex)); b.Freeze(); return b; }

    static void Place(UIElement e, double x, double y) { Canvas.SetLeft(e, x); Canvas.SetTop(e, y); }

    static Ellipse Disc(double cx, double cy, double r, Brush fill, Brush stroke = null, double st = 0) =>
        new() { Width = r * 2, Height = r * 2, Fill = fill, Stroke = stroke, StrokeThickness = st };

    /// <summary>Tonearm drawn along +x from the pivot (0,0); the needle tip is at (ArmLen, 0).</summary>
    static Canvas BuildArm(bool shadow, RotateTransform rot)
    {
        var c = new Canvas { Width = 0, Height = 0, IsHitTestVisible = false };
        Brush rod = shadow ? Solid("#000000") : Grad("#F1EBDD", "#8E8878", 0, 0, 0, 1);
        Brush brass = shadow ? Solid("#000000") : Grad("#E0C78F", "#7A5F2E", 0, 0, 0, 1);
        Brush dark = shadow ? Solid("#000000") : Solid("#1E1812");

        Rectangle R(double x, double y, double w, double h, Brush f, double rad = 2) =>
            new() { Width = w, Height = h, Fill = f, RadiusX = rad, RadiusY = rad };

        void Add(UIElement e, double x, double y) { Place(e, x, y); c.Children.Add(e); }

        Add(R(-36, -8, 24, 16, brass, 4), -36, -8);          // counterweight
        Add(R(-14, -3, 20, 6, rod, 2), -14, -3);             // rear stub
        Add(R(4, -2.4, ArmLen - 24, 4.8, rod, 2.4), 4, -2.4); // main tube

        var head = new Polygon
        {
            Points = new PointCollection { new(ArmLen - 26, -3.6), new(ArmLen - 5, -7.5), new(ArmLen + 5, 0), new(ArmLen - 5, 7.5), new(ArmLen - 26, 3.6) },
            Fill = brass
        };
        c.Children.Add(head);
        if (!shadow)
        {
            Add(R(ArmLen - 16, -4.4, 11, 8.8, dark, 2), ArmLen - 16, -4.4);          // cartridge
            Add(Disc(0, 0, 1.9, Solid("#E8E0D0")), ArmLen + 0.1, -1.9);              // needle tip
            Add(Disc(0, 0, 3.2, Grad("#E0C78F", "#6B5430")), -3.2, -3.2);            // pivot bearing
        }
        return c;
    }

    UIElement MakePivotCap()
    {
        var cap = Disc(0, 0, 6.5, Grad("#EAD9AA", "#7A5F2E"), Solid("#3A2B16"), 0.8);
        cap.IsHitTestVisible = false;
        Place(cap, Piv.X - 6.5, Piv.Y - 6.5);
        return cap;
    }

    // ------------------------------------------------------------------ arm kinematics
    void UpdateArm()
    {
        if (_armScale == null) return;      // during construction
        double prog = PhiOut + (PhiIn - PhiOut) * Math.Clamp(Progress, 0, 1);
        double ang = prog + (PhiRest - prog) * Math.Clamp(Swing, 0, 1);
        double lift = Math.Clamp(Lift, 0, 1);
        _armRot.Angle = ang;
        _shadowRot.Angle = ang;
        _armScale.ScaleX = _armScale.ScaleY = 1 + 0.07 * lift;       // a lifted arm is closer to the viewer
        Canvas.SetLeft(_shadow, Piv.X + 2 + 11 * lift);               // ... and its shadow drifts away
        Canvas.SetTop(_shadow, Piv.Y + 3 + 15 * lift);
        _shadow.Opacity = 0.5 - 0.28 * lift;
    }

    // ------------------------------------------------------------------ lifecycle
    void Attach()
    {
        if (_attached || A == null) return;
        _attached = true;
        C.Changed += OnCore;
        Sync();
    }

    void Detach()
    {
        if (!_attached) return;
        _attached = false;
        C.Changed -= OnCore;
        _chgTok++; _armTok++;
        _changing = false;
        _a.V.Suspend(); _b.V.Suspend();
    }

    /// <summary>Jump straight to the correct visual state (no animation).</summary>
    void Sync()
    {
        _chgTok++; _armTok++;
        _changing = false;
        FreezeAll();
        _shown = C.Current;
        _artShown = C.Artwork;
        _front = _a;
        _b.V.Apply(PlayState.NoMedia, S.Animations);
        RestoreFront();
        _a.V.SetArtwork(C.Artwork, 0);
        _progAnimating = false;
        Progress = Fraction();
        ApplyState(true);
    }

    // ------------------------------------------------------------------ core events
    void OnCore(CoreChange c)
    {
        switch (c)
        {
            case CoreChange.Position: if (!_changing) Follow(); break;
            case CoreChange.State: if (!_changing) ApplyState(false); break;
            case CoreChange.Track: OnTrack(); break;
        }
    }

    static double Fraction()
    {
        double d = C.Duration.TotalSeconds;
        return d > 0 ? Math.Clamp(C.Position.TotalSeconds / d, 0, 1) : 0;
    }

    void Follow()
    {
        if (C.State is PlayState.Playing or PlayState.Paused or PlayState.Loading) SetProgress(Fraction(), false);
    }

    /// <summary>Moves the needle to the song position. Ordinary ticks just nudge it; seeks are eased.</summary>
    void SetProgress(double t, bool instant)
    {
        if (_progAnimating) return;
        double cur = Progress;
        if (Math.Abs(t - cur) < 0.0004) return;
        if (instant || F <= 0 || Math.Abs(t - cur) < 0.02) { Progress = t; return; }
        _progAnimating = true;
        _ = Anim(this, ProgressProperty, t, 380, new CubicEase { EasingMode = EasingMode.EaseOut })
            .ContinueWith(_ => { _progAnimating = false; }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void OnTrack()
    {
        var t = C.Current;
        if (ReferenceEquals(t, _shown))                   // same song: only the label can have changed (cover edited)
        {
            if (!ReferenceEquals(C.Artwork, _artShown) && !_changing)
            {
                _artShown = C.Artwork;
                _front.V.SetArtwork(C.Artwork, F > 0 ? 250 : 0);
            }
            return;                                       // restart of the same song: the arm just follows the position
        }
        _shown = t;
        if (t == null)                                    // queue emptied
        {
            _chgTok++; _changing = false;
            _front.V.SetArtwork(null, F > 0 ? 250 : 0);
            ArmTo(1, 1);
            return;
        }
        if (F <= 0)
        {
            _chgTok++; _changing = false;
            _artShown = C.Artwork;
            _front.V.SetArtwork(C.Artwork, 0);
            Progress = 0;
            ApplyState(true);
            return;
        }
        ChangeRecord();
    }

    // ------------------------------------------------------------------ play / pause / stop
    void ApplyState(bool instant)
    {
        var st = C.State;
        _front.V.Apply(st, S.Animations);

        double swing, lift;
        switch (st)
        {
            case PlayState.Playing: swing = 0; lift = 0; break;
            case PlayState.Loading:
                if (!instant) return;                      // keep whatever the arm is doing while a file opens
                swing = 0; lift = 0; break;
            case PlayState.Paused: swing = 0; lift = 1; break;
            default: swing = 1; lift = 1; break;           // stopped / no media / error: park
        }

        if (instant) { FreezeAll(); Swing = swing; Lift = lift; return; }

        if (st == PlayState.Playing && Swing > 0.5)        // coming from the rest: start where the song is
        {
            _progAnimating = false;
            BeginAnimation(ProgressProperty, null);
            Progress = Fraction();
        }
        ArmTo(swing, lift);
    }

    async void ArmTo(double swingT, double liftT)
    {
        int tok = ++_armTok;
        double f = F;
        FreezeAll();
        if (f <= 0) { Swing = swingT; Lift = liftT; return; }
        try
        {
            if (Math.Abs(Swing - swingT) > 0.005)
            {
                if (Lift < 0.995) { await Anim(this, LiftProperty, 1, 190 * f); if (tok != _armTok) return; }
                await Anim(this, SwingProperty, swingT, 620 * f, new CubicEase { EasingMode = EasingMode.EaseInOut });
                if (tok != _armTok) return;
            }
            if (Math.Abs(Lift - liftT) > 0.005)
            {
                bool down = liftT < Lift;
                await Anim(this, LiftProperty, liftT, (down ? 260 : 190) * f,
                    new CubicEase { EasingMode = down ? EasingMode.EaseIn : EasingMode.EaseOut });
            }
        }
        catch (Exception ex) { Paths.Log(ex); }
    }

    // ------------------------------------------------------------------ the record change
    async void ChangeRecord()
    {
        int tok = ++_chgTok;
        _changing = true;
        ++_armTok;
        FreezeAll();
        RestoreFront();

        double f = F;
        var old = _front;
        var nw = ReferenceEquals(old, _a) ? _b : _a;
        var art = C.Artwork;
        _artShown = art;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        try
        {
            // 1. needle lifts, the old record slows to a stop
            old.V.Apply(PlayState.Paused, S.Animations);
            await Task.WhenAll(Anim(this, LiftProperty, 1, 200 * f), Task.Delay((int)(260 * f)));
            if (tok != _chgTok) return;

            // 2. arm swings out of the way, the old record leaves
            var swing = Anim(this, SwingProperty, 1, 480 * f, ease);
            await Task.Delay((int)(140 * f));
            if (tok != _chgTok) return;
            var leave = AnimRecord(old, -330, 0, 0.9, 0, 380 * f, new CubicEase { EasingMode = EasingMode.EaseIn });
            await Task.WhenAll(swing, leave);
            if (tok != _chgTok) return;
            old.V.Apply(PlayState.NoMedia, S.Animations);
            old.V.Visibility = Visibility.Collapsed;

            // 3. the new record comes in from below and settles
            nw.V.SetArtwork(art, 0);
            nw.V.Apply(PlayState.NoMedia, S.Animations);
            SetRec(nw, 0, 340, 0.94, 1);
            nw.V.Visibility = Visibility.Visible;
            _progAnimating = false;
            Progress = 0;
            await AnimRecord(nw, 0, 0, 1, 1, 460 * f, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });
            if (tok != _chgTok) return;
            _front = nw;

            // 4. spin up, arm swings in, needle lowers (whatever the real playback state is by now)
            _changing = false;
            ApplyState(false);
        }
        catch (Exception ex)
        {
            Paths.Log(ex);
            _changing = false;
            Sync();
        }
    }

    Task AnimRecord(Slot s, double x, double y, double scale, double opacity, double ms, IEasingFunction e) =>
        Task.WhenAll(
            Anim(s.T, TranslateTransform.XProperty, x, ms, e),
            Anim(s.T, TranslateTransform.YProperty, y, ms, e),
            Anim(s.S, ScaleTransform.ScaleXProperty, scale, ms, e),
            Anim(s.S, ScaleTransform.ScaleYProperty, scale, ms, e),
            Anim(s.V, OpacityProperty, opacity, ms, e));

    static void SetRec(Slot s, double x, double y, double scale, double opacity)
    {
        s.T.X = x; s.T.Y = y; s.S.ScaleX = s.S.ScaleY = scale; s.V.Opacity = opacity;
    }

    /// <summary>Puts the front record back on the platter (after an interrupted change) and hides the other one.</summary>
    void RestoreFront()
    {
        var other = ReferenceEquals(_front, _a) ? _b : _a;
        SetRec(_front, 0, 0, 1, 1);
        _front.V.Visibility = Visibility.Visible;
        SetRec(other, 0, 0, 1, 1);
        other.V.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ animation plumbing
    /// <summary>Runs a double animation; completes when it ends or is replaced. Final value is committed as base value.</summary>
    Task Anim(IAnimatable target, DependencyProperty dp, double to, double ms, IEasingFunction ease = null)
    {
        var tcs = new TaskCompletionSource<bool>();
        var key = (target, dp);
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(Math.Max(1, ms)))
        {
            EasingFunction = ease ?? new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.Stop
        };
        _run[key] = a;
        a.Completed += (_, _) =>
        {
            if (_run.TryGetValue(key, out var cur) && ReferenceEquals(cur, a))
            {
                _run.Remove(key);
                ((DependencyObject)target).SetValue(dp, to);
            }
            tcs.TrySetResult(true);
        };
        target.BeginAnimation(dp, a);
        return tcs.Task;
    }

    /// <summary>Stops everything that is animating but keeps each property at its current visual value.</summary>
    void FreezeAll()
    {
        foreach (var kv in _run.ToArray())
        {
            var (target, dp) = kv.Key;
            var d = (DependencyObject)target;
            object v = d.GetValue(dp);
            target.BeginAnimation(dp, null);
            d.SetValue(dp, v);
        }
        _run.Clear();
        _progAnimating = false;
    }
}
