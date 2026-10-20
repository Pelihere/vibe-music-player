using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PortablePlayer;

/// <summary>
/// Vibe's musical signature. A ScoreLayer sits INSIDE a control's template, behind its content (text, icons, artwork stay on top and readable).
/// While the pointer is over the control (or the keyboard focus is on it) the whole effect fades in at once: little notes that slowly drift
/// (right to left, with a gentle wave) and twinkling four-point star sparkles. It keeps going until the pointer leaves; then it fades out.
///
/// Usage: put  &lt;local:ScoreLayer/&gt;  in a template (before the ContentPresenter), or create one in code and pass the host.
/// It is never hit-testable, so clicks, drag and drop, selection and keyboard navigation are untouched.
/// Speed / on-off: Settings → Appearance → Musical hover effect (Off, Slow, Medium, Fast).
/// </summary>
public sealed class ScoreLayer : FrameworkElement
{
    public static readonly DependencyProperty PhaseProperty = DependencyProperty.Register(nameof(Phase), typeof(double), typeof(ScoreLayer),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0..1, repeating: position in the flow cycle.</summary>
    public double Phase { get => (double)GetValue(PhaseProperty); set => SetValue(PhaseProperty, value); }

    /// <summary>The element whose hover / focus drives this layer. Default: the templated parent, else the visual parent.</summary>
    public FrameworkElement Host { get; set; }

    FrameworkElement _host;
    bool _hostMouse, _hostKey, _flowing;
    int _ticket;

    // prepared per hover (depends on the size and the chosen phrase)
    bool _ready;
    double _g, _left, _period, _centerY, _waveK;
    (int Step, char Kind)[] _notes = Array.Empty<(int, char)>();
    double[] _centers = Array.Empty<double>();
    DrawingGroup[] _shapes = new DrawingGroup[6];
    Geometry _star;
    Brush _inkBrush, _starBrush;
    double _starK;
    int _stars;
    int _hovers;

    public ScoreLayer()
    {
        IsHitTestVisible = false;
        Focusable = false;
        SnapsToDevicePixels = true;
        ClipToBounds = true;
        Opacity = 0;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ------------------------------------------------------------------ hooking up to the host
    void OnLoaded(object s, RoutedEventArgs e)
    {
        Unhook();
        _host = Host ?? TemplatedParent as FrameworkElement ?? Parent as FrameworkElement;
        if (_host == null) return;
        _host.MouseEnter += HostEnter; _host.MouseLeave += HostLeave;
        _host.GotKeyboardFocus += HostFocus; _host.LostKeyboardFocus += HostBlur;
        if (_host.IsMouseOver) HostEnter(null, null);
    }

    void OnUnloaded(object s, RoutedEventArgs e) { Unhook(); Stop(true); }

    void Unhook()
    {
        if (_host == null) return;
        _host.MouseEnter -= HostEnter; _host.MouseLeave -= HostLeave;
        _host.GotKeyboardFocus -= HostFocus; _host.LostKeyboardFocus -= HostBlur;
        _host = null;
    }

    void HostEnter(object s, MouseEventArgs e) { _hostMouse = true; Start(); }
    void HostLeave(object s, MouseEventArgs e) { _hostMouse = false; if (!_hostKey) FadeOut(); }
    void HostFocus(object s, KeyboardFocusChangedEventArgs e)
    {
        if (InputManager.Current.MostRecentInputDevice is not KeyboardDevice) return;     // focus from a mouse click does not count
        _hostKey = true; Start();
    }
    void HostBlur(object s, KeyboardFocusChangedEventArgs e) { _hostKey = false; if (!_hostMouse) FadeOut(); }

    // ------------------------------------------------------------------ settings
    /// <summary>Pixels per second the notes drift; 0 = effect switched off.</summary>
    static double FlowSpeed()
    {
        string mode = "Slow";
        try { mode = App.Instance.Settings.ScoreEffect ?? "Slow"; } catch { }
        return mode switch { "Off" => 0, "Medium" => 48, "Fast" => 85, _ => 22 };
    }

    // ------------------------------------------------------------------ animation
    void Start()
    {
        double speed = FlowSpeed();
        if (speed <= 0 || _host == null || !_host.IsEnabled || ActualWidth < 56 || ActualHeight < 22) return;
        _ticket++;
        _hovers++;
        BeginAnimation(OpacityProperty, null);
        BeginAnimation(PhaseProperty, null);
        Phase = 0;

        Prepare(speed);

        var mode = AnimMode.Full;
        try { mode = App.Instance.Settings.Animations; } catch { }
        bool motion = mode != AnimMode.Off && SystemParameters.ClientAreaAnimation;     // reduced motion: no drifting / twinkling, only a fade

        if (!motion) { Opacity = 1; return; }
        BeginAnimation(OpacityProperty, new DoubleAnimation(Opacity, 1, TimeSpan.FromMilliseconds(mode == AnimMode.Reduced ? 140 : 220))
        { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } });          // fades in everywhere at once

        if (mode == AnimMode.Full)                                                       // flow + twinkle: slow, linear, until the pointer leaves
        {
            _flowing = true;
            BeginAnimation(PhaseProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(Math.Max(3, _period / speed)))
            { RepeatBehavior = RepeatBehavior.Forever });
        }
    }

    void FadeOut()
    {
        if (Opacity <= 0) return;
        int mine = ++_ticket;
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(260));
        fade.Completed += (_, _) => { if (mine == _ticket) Stop(false); };               // not when the pointer came back meanwhile
        BeginAnimation(OpacityProperty, fade);
    }

    void Stop(bool immediate)
    {
        BeginAnimation(PhaseProperty, null);
        if (immediate) BeginAnimation(OpacityProperty, null);
        _flowing = false;
        Phase = 0;
        if (immediate) Opacity = 0;
        _ready = false;
    }

    // ------------------------------------------------------------------ phrases (predefined; nothing random)
    // Step 0 = bottom staff line, one step = half a line gap (odd steps are spaces); kind: q quarter, h half, e eighth.
    static readonly (int Step, char Kind)[][] Phrases =
    {
        new[] { (2,'q'), (4,'q'), (6,'e'), (5,'e'), (7,'q'), (5,'q'), (3,'h'), (4,'q'), (6,'q'), (8,'e') },
        new[] { (6,'q'), (5,'e'), (4,'e'), (2,'q'), (4,'q'), (7,'h'), (8,'q'), (6,'e'), (5,'e'), (3,'q') },
        new[] { (0,'q'), (3,'e'), (5,'e'), (4,'q'), (7,'q'), (6,'h'), (2,'q'), (4,'e'), (5,'e'), (8,'q') },
        new[] { (5,'e'), (7,'e'), (8,'q'), (6,'q'), (4,'h'), (3,'q'), (5,'q'), (2,'e'), (4,'e'), (6,'q') },
    };

    static double Weight(char k) => k == 'h' ? 1.4 : k == 'e' ? 0.85 : 1.0;

    /// <summary>
    /// Lays the phrase out for the real size: symbols keep their proportions (they scale with the staff gap only, which comes from the height);
    /// a wider element gets more notes and wider spacing, a narrow one a short phrase.
    /// </summary>
    void Prepare(double speed)
    {
        double w = ActualWidth, h = ActualHeight;
        var accent = PickColor();
        var ink = new SolidColorBrush(Color.FromArgb(0x78, accent.R, accent.G, accent.B)); ink.Freeze();
        var starBrush = new SolidColorBrush(Color.FromArgb(0xD0, accent.R, accent.G, accent.B)); starBrush.Freeze();
        _starBrush = starBrush; _inkBrush = ink;
        var pen = new Pen(ink, 1.35) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }; pen.Freeze();
        _star ??= MakeStar();

        _g = Math.Clamp(h * 0.13, 2.8, 4.6);
        _g = Math.Min(_g, (h - 6) / 4.2);
        _centerY = h / 2;

        _left = 12;
        double usable = Math.Max(20, w - 2 * _left);
        int n = (int)Math.Clamp(Math.Floor(usable / (6.5 * _g)), 2, 26);
        var phrase = Phrases[(_hovers + (RuntimeHelpers.GetHashCode(_host) & 0x7fffffff)) % Phrases.Length];

        _notes = new (int, char)[n];
        double total = 0;
        for (int i = 0; i < n; i++) { _notes[i] = phrase[i % phrase.Length]; total += Weight(_notes[i].Kind); }
        _period = usable * (1 + 1.0 / n);                                                // one empty slot, so the notes wrap round evenly
        double unit = _period / (total + 1.0), acc = 0;
        _centers = new double[n];
        for (int i = 0; i < n; i++) { double wt = Weight(_notes[i].Kind); _centers[i] = (acc + wt / 2) * unit; acc += wt; }
        double cycle = _period / Math.Max(1, speed);
        _waveK = Math.Max(2, Math.Round(cycle / 5.0));                                  // whole wave / twinkle cycles per loop: no jump when it repeats
        _starK = Math.Max(3, Math.Round(cycle / 2.4));
        _stars = (int)Math.Clamp(Math.Round(w / 48), 3, 16);

        foreach (var kind in new[] { 'q', 'h', 'e' })
        {
            int k = kind == 'q' ? 0 : kind == 'h' ? 1 : 2;
            _shapes[k * 2] = MakeNote(kind, true, _g, ink, pen);
            _shapes[k * 2 + 1] = MakeNote(kind, false, _g, ink, pen);
        }
        _ready = true;
    }

    /// <summary>Four-point sparkle, radius 1 (long points), centred on 0,0.</summary>
    static Geometry MakeStar()
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            const double inner = 0.26;
            for (int i = 0; i < 8; i++)
            {
                double ang = i * Math.PI / 4 - Math.PI / 2, r = i % 2 == 0 ? 1.0 : inner;
                var pt = new Point(Math.Cos(ang) * r, Math.Sin(ang) * r);
                if (i == 0) c.BeginFigure(pt, true, true); else c.LineTo(pt, true, false);
            }
        }
        g.Freeze();
        return g;
    }

    static DrawingGroup MakeNote(char kind, bool stemUp, double g, Brush ink, Pen pen)
    {
        var dg = new DrawingGroup();
        using (var dc = dg.Open())
        {
            double rx = 0.68 * g, ry = 0.5 * g;                                         // fixed head proportions
            var head = new EllipseGeometry(new Point(0, 0), rx, ry) { Transform = new RotateTransform(-20) };
            dc.DrawGeometry(kind == 'h' ? null : ink, pen, head);
            double sx = stemUp ? rx * 0.92 : -rx * 0.92, len = 3.3 * g, ey = stemUp ? -len : len;
            dc.DrawLine(pen, new Point(sx, stemUp ? -0.1 * g : 0.1 * g), new Point(sx, ey));
            if (kind == 'e')                                                            // flag
            {
                double d = stemUp ? 1 : -1;
                var f = new StreamGeometry();
                using (var c = f.Open())
                {
                    c.BeginFigure(new Point(sx, ey), false, false);
                    c.BezierTo(new Point(sx + 0.2 * g, ey + d * 0.9 * g), new Point(sx + 1.5 * g, ey + d * 1.1 * g), new Point(sx + 1.0 * g, ey + d * 2.3 * g), true, false);
                }
                f.Freeze();
                dc.DrawGeometry(null, pen, f);
            }
        }
        dg.Freeze();
        return dg;
    }

    // ------------------------------------------------------------------ drawing
    protected override void OnRender(DrawingContext dc)
    {
        if (!_ready || Opacity <= 0) return;
        double w = ActualWidth, h = ActualHeight;
        double radius = TryFindResource("RadiusMd") is CornerRadius cr ? Math.Max(0, cr.TopLeft - 1) : 0;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), radius, radius));          // stay inside the rounded shape of the control

        double phase = _flowing ? Phase : 0;

        // sparkles: fixed, evenly scattered positions (golden-ratio spacing, nothing random); each one twinkles on its own beat
        for (int j = 0; j < _stars; j++)
        {
            double fx = (j * 0.6180339887 + 0.13) % 1.0, fy = (j * 0.7548776662 + 0.31) % 1.0;
            double x = 8 + fx * Math.Max(1, w - 16), y = h * (0.18 + 0.64 * fy);
            double size = 2.4 + 2.6 * ((j * 0.43) % 1.0);                                 // radius in px: 2.4 .. 5
            double t = _flowing ? Math.Abs(Math.Sin(Math.PI * (phase * _starK + j * 0.37))) : 0.75;
            double sc = size * (0.3 + 0.7 * t);
            dc.PushOpacity(0.25 + 0.75 * t);
            dc.PushTransform(new TranslateTransform(x, y));
            dc.PushTransform(new ScaleTransform(sc, sc));
            dc.DrawGeometry(_starBrush, null, _star);
            dc.Pop(); dc.Pop(); dc.Pop();
        }

        // notes: drifting right to left with a slight wave, fading at the edges
        double edge = 20;
        for (int i = 0; i < _notes.Length; i++)
        {
            var (step, kind) = _notes[i];
            int shape = (kind == 'q' ? 0 : kind == 'h' ? 1 : 2) * 2 + (step < 4 ? 0 : 1);
            double baseX = _left + (((_centers[i] - phase * _period) % _period) + _period) % _period;
            double wave = _flowing ? Math.Sin((phase * _waveK + i * 0.9) * 2 * Math.PI) * 0.3 * _g : 0;
            double y = _centerY - (step - 4) * _g / 2 + wave;
            for (int k = -1; k <= 1; k++)
            {
                double px = baseX + k * _period;
                if (px < -2 * _g || px > w + 2 * _g) continue;
                double a = Math.Clamp(Math.Min(px, w - px) / edge, 0, 1);
                if (a <= 0.02) continue;
                if (a < 1) dc.PushOpacity(a);
                dc.PushTransform(new TranslateTransform(px, y));
                dc.DrawDrawing(_shapes[shape]);
                dc.Pop();
                if (a < 1) dc.Pop();
            }
        }
        dc.Pop();                                                                           // shape clip
    }

    /// <summary>Accent colour of the theme; the "on accent" colour where the control is filled with the accent.</summary>
    Color PickColor()
    {
        Color Res(string key, Color fallback) => TryFindResource(key) is SolidColorBrush b ? b.Color : fallback;
        var accent = Res("Accent", Colors.Orange);
        bool onAccent = false;
        if (_host is Control c)
        {
            if (c.Background is LinearGradientBrush) onAccent = true;                       // AccentGradient (primary buttons)
            else if (c.Background is SolidColorBrush sb && sb.Color == accent) onAccent = true;
            if (c is RadioButton rb && rb.IsChecked == true && ReferenceEquals(c.Style, TryFindResource("Segment"))) onAccent = true;
        }
        return onAccent ? Res("OnAccent", Colors.Black) : accent;
    }
}
