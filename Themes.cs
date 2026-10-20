using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PortablePlayer;

/// <summary>Design tokens of one Main Window theme. The Gramophone never reads any of this.</summary>
public sealed record ThemeDef(
    string Name, string Glyph,
    string Bg, string Surface, string Surface2,
    string Accent, string Highlight, string OnAccent,
    string Fg, string Muted, string Border,
    string TitleBg, string Shadow,
    double RadiusSm, double RadiusMd, double RadiusLg, double BorderPx,
    string Texture, string TexColor,
    double ShadowPx = 4, string Tone3 = "#8CC9A8", string Tone4 = "#F6D8A8");

/// <summary>
/// Central theme store. Every colour of the Main Window comes from the brushes registered here
/// (Bg, Surface, Surface2, Accent, Fg, Muted, Border, ...); switching a theme cross-fades those same brush
/// objects, so every DynamicResource consumer follows without rebuilding any UI.
/// </summary>
public static class ThemeManager
{
    // Accent = primary accent, Highlight = secondary accent (second stop of the accent gradient, errors, highlights).
    // Dark Teal is first on purpose: it is the default and the fallback for any unknown / retired theme name.
    public static readonly ThemeDef[] All =
    {
        // Neo-brutalist family: bold near-black outlines, hard offset shadows, warm muted colours, grid-paper background, fine cross-hatch on surfaces.
        // Tone3 / Tone4 are the extra card colours (statistics tiles, accents); text on every accent colour is OnAccent.
        new("Dark Teal", "◼",
            Bg: "#0F3433", Surface: "#17504D", Surface2: "#1F625E",
            Accent: "#F2B544", Highlight: "#F07F63", OnAccent: "#1A1205",
            Fg: "#FBF3DF", Muted: "#9CC2B8", Border: "#0A0A0A",
            TitleBg: "#0B2726", Shadow: "#000000",
            RadiusSm: 4, RadiusMd: 6, RadiusLg: 10, BorderPx: 2, Texture: "paper", TexColor: "#16FBF3DF",
            ShadowPx: 4, Tone3: "#8CC9A8", Tone4: "#F6D8A8"),

        new("Dark Blue", "◆",
            Bg: "#101C3A", Surface: "#1A2D5A", Surface2: "#243B73",
            Accent: "#F4C95D", Highlight: "#FF8F70", OnAccent: "#1B1403",
            Fg: "#FFF6E4", Muted: "#9FB0D6", Border: "#080A12",
            TitleBg: "#0B1429", Shadow: "#000000",
            RadiusSm: 4, RadiusMd: 6, RadiusLg: 10, BorderPx: 2, Texture: "paper", TexColor: "#16FFF6E4",
            ShadowPx: 4, Tone3: "#7FB8E8", Tone4: "#E7B7C8"),

        new("Plasma", "✺",
            Bg: "#1E1030", Surface: "#32184F", Surface2: "#44226A",
            Accent: "#FF8AD8", Highlight: "#FFC857", OnAccent: "#2A0A24",
            Fg: "#FFF1FA", Muted: "#C0A3DB", Border: "#0B0612",
            TitleBg: "#150A23", Shadow: "#000000",
            RadiusSm: 4, RadiusMd: 6, RadiusLg: 10, BorderPx: 2, Texture: "paper", TexColor: "#16FFF1FA",
            ShadowPx: 4, Tone3: "#9D8CFF", Tone4: "#7DE0C6"),

        new("Terracotta", "▲",
            Bg: "#3A1F17", Surface: "#55301F", Surface2: "#6B3E29",
            Accent: "#F0A35E", Highlight: "#E0705A", OnAccent: "#2A1307",
            Fg: "#FFF0DD", Muted: "#D2AD93", Border: "#0D0705",
            TitleBg: "#2B1710", Shadow: "#000000",
            RadiusSm: 4, RadiusMd: 6, RadiusLg: 10, BorderPx: 2, Texture: "paper", TexColor: "#16FFF0DD",
            ShadowPx: 4, Tone3: "#B7C98A", Tone4: "#E8D3A0"),
    };

    static readonly string[] BrushKeys =
    {
        "Bg", "Surface", "Surface2", "Accent", "Highlight", "OnAccent", "Fg", "Muted", "Border",
        "TitleBg", "Shadow", "Hover", "Select", "RowPlaying", "ScrollThumb", "Tone3", "Tone4"
    };

    static readonly Dictionary<string, SolidColorBrush> Brushes = new();
    static readonly Dictionary<string, Color> Cur = new();
    static readonly Dictionary<string, int> Tokens = new();
    static System.Windows.Threading.DispatcherTimer _fade;

    public static string Current { get; private set; } = "Dark Teal";
    public static event Action Changed;

    /// <summary>Creates the brushes and publishes them as application resources.</summary>
    public static void Init(string name)
    {
        var res = Application.Current.Resources;
        foreach (var k in BrushKeys)
        {
            var b = new SolidColorBrush(Colors.Transparent);
            Brushes[k] = b;
            Cur[k] = Colors.Transparent;
            res[k] = b;
        }
        Apply(name, false);
    }

    public static ThemeDef Find(string name) => All.FirstOrDefault(t => t.Name == name) ?? All[0];

    public static Color Col(string key) => Cur[key];

    /// <summary>
    /// Puts a colour into a theme brush. WPF may hand back frozen brushes for application resources, which cannot be
    /// animated or modified - in that case the brush is replaced by a new one (DynamicResource consumers follow).
    /// Everything that wants to follow the theme must therefore bind with a resource reference, never hold a brush.
    /// </summary>
    static void Put(string key, Color c)
    {
        Cur[key] = c;
        if (key == "Bg")        // colour resources for gradients (artist hero fade)
        {
            Application.Current.Resources["BgColor"] = c;
            Application.Current.Resources["BgColor0"] = Color.FromArgb(0, c.R, c.G, c.B);
            Application.Current.Resources["BgColor85"] = Color.FromArgb(0xE0, c.R, c.G, c.B);
        }
        var b = Brushes[key];
        if (!b.IsFrozen)
        {
            try { b.BeginAnimation(SolidColorBrush.ColorProperty, null); b.Color = c; return; }
            catch (InvalidOperationException) { }
        }
        var nb = new SolidColorBrush(c);
        nb.Freeze();
        Brushes[key] = nb;
        Application.Current.Resources[key] = nb;
    }

    /// <summary>Row height of song lists (Settings → Appearance → density).</summary>
    public static void ApplyDensity()
    {
        bool compact = ((Application.Current as App)?.Settings?.Density) == "Compact";
        Application.Current.Resources["RowHeight"] = compact ? 48.0 : 58.0;
    }

    static Color Lerp(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(a.A + (b.A - a.A) * t), (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    public static void Apply(string name, bool animate)
    {
        var d = Find(name);
        Current = d.Name;
        var res = Application.Current.Resources;

        Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
        Color A(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
        var fg = C(d.Fg); var accent = C(d.Accent);
        var bg = C(d.Bg);
        bool light = (0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B) > 140;

        var target = new Dictionary<string, Color>
        {
            ["Bg"] = bg, ["Surface"] = C(d.Surface), ["Surface2"] = C(d.Surface2),
            ["Accent"] = accent, ["Highlight"] = C(d.Highlight), ["OnAccent"] = C(d.OnAccent),
            ["Fg"] = fg, ["Muted"] = C(d.Muted), ["Border"] = C(d.Border),
            ["TitleBg"] = C(d.TitleBg), ["Shadow"] = C(d.Shadow),
            ["Hover"] = A(fg, light ? (byte)0x14 : (byte)0x16),
            ["Select"] = A(accent, light ? (byte)0x38 : (byte)0x34),
            ["RowPlaying"] = A(accent, light ? (byte)0x26 : (byte)0x1E),
            ["ScrollThumb"] = A(fg, 0x44),
            ["Tone3"] = C(d.Tone3), ["Tone4"] = C(d.Tone4),
        };

        // non-colour tokens switch immediately
        res["RadiusSm"] = new CornerRadius(d.RadiusSm);
        res["RadiusMd"] = new CornerRadius(d.RadiusMd);
        res["RadiusLg"] = new CornerRadius(d.RadiusLg);
        res["BorderThin"] = new Thickness(d.BorderPx);
        res["Texture"] = MakeTexture(d.Texture, d.TexColor);
        res["SurfaceTexture"] = MakeTexture("hatch", light ? "#1E000000" : "#26000000");       // fine cross-hatch on cards and panels
        var shadow = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 0, ShadowDepth = d.ShadowPx, Direction = 315, Opacity = 1, Color = C(d.Shadow)   // hard offset shadow, no blur
        };
        shadow.Freeze();
        res["CardShadow"] = shadow;
        res["ShadowPx"] = d.ShadowPx;
        var grad = new LinearGradientBrush(accent, C(d.Highlight), 0);       // primary buttons, play button, active markers
        grad.Freeze();
        res["AccentGradient"] = grad;
        ApplyDensity();

        _fade?.Stop();
        var mode = (Application.Current as App)?.Settings?.Animations ?? AnimMode.Full;
        if (!animate || mode == AnimMode.Off)
        {
            foreach (var kv in target) Put(kv.Key, kv.Value);
            Changed?.Invoke();
            return;
        }

        // short cross-fade: a few eased steps from the current colours to the target ones
        var from = new Dictionary<string, Color>(Cur);
        int steps = mode == AnimMode.Full ? 10 : 5, i = 0;
        _fade = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(32) };
        _fade.Tick += (_, _) =>
        {
            i++;
            double x = Math.Min(1.0, (double)i / steps);
            double t = x * x * (3 - 2 * x);                 // smoothstep
            foreach (var kv in target) Put(kv.Key, Lerp(from[kv.Key], kv.Value, t));
            if (i >= steps) { _fade.Stop(); Changed?.Invoke(); }
        };
        _fade.Start();
    }

    // ------------------------------------------------------------------ subtle surface textures
    static Brush MakeTexture(string kind, string colorHex)
    {
        Color col = (Color)ColorConverter.ConvertFromString(colorHex);
        var pen = new Pen(new SolidColorBrush(col), 1); pen.Freeze();
        var dark = new Pen(new SolidColorBrush(Color.FromArgb(0x12, 0, 0, 0)), 1); dark.Freeze();

        static GeometryDrawing Line(Pen p, double x1, double y1, double x2, double y2) =>
            new(null, p, new LineGeometry(new Point(x1, y1), new Point(x2, y2)));
        static Brush Tile(double w, double h, params Drawing[] parts)
        {
            var g = new DrawingGroup();
            g.Children.Add(new GeometryDrawing(System.Windows.Media.Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, w, h))));
            foreach (var part in parts) g.Children.Add(part);
            var b = new DrawingBrush(g)
            {
                TileMode = TileMode.Tile, Stretch = Stretch.None,
                Viewport = new Rect(0, 0, w, h), ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, w, h), ViewboxUnits = BrushMappingMode.Absolute
            };
            b.Freeze();
            return b;
        }

        switch (kind)
        {
            case "paper":  // grid paper: fine lines every 24 px, a slightly stronger one every 4th
            {
                var strong = new Pen(new SolidColorBrush(Color.FromArgb((byte)Math.Min(255, col.A + 10), col.R, col.G, col.B)), 1); strong.Freeze();
                return Tile(96, 96, Enumerable.Range(0, 4).SelectMany(i => new Drawing[]
                {
                    Line(i == 0 ? strong : pen, 0, i * 24 + 0.5, 96, i * 24 + 0.5),
                    Line(i == 0 ? strong : pen, i * 24 + 0.5, 0, i * 24 + 0.5, 96)
                }).ToArray());
            }
            case "hatch":  // fine regular cross-hatching
            {
                var fine = new Pen(new SolidColorBrush(col), 0.6); fine.Freeze();
                return Tile(6, 6, Line(fine, 0, 6, 6, 0), Line(fine, 0, 0, 6, 6));
            }
            case "metal":  // fine diagonal brushing
                return Tile(6, 6, Line(pen, 0, 6, 6, 0), Line(dark, -3, 3, 3, -3));
            case "dots":
                return Tile(20, 20,
                    new GeometryDrawing(new SolidColorBrush(col), null, new EllipseGeometry(new Point(5, 5), 1.6, 1.6)),
                    new GeometryDrawing(new SolidColorBrush(col), null, new EllipseGeometry(new Point(15, 15), 1.6, 1.6)));
            case "grid":   // faint grid
                return Tile(36, 36, Line(pen, 0, 0, 36, 0), Line(pen, 0, 0, 0, 36));
            default:
                return System.Windows.Media.Brushes.Transparent;
        }
    }
}
