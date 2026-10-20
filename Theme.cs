using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PortablePlayer;

/// <summary>Soft, muted 3-colour palette derived from album art (dark base so light text always stays readable).</summary>
public sealed class Palette
{
    public Color Primary { get; init; }
    public Color Secondary { get; init; }
    public Color Accent { get; init; }

    public static readonly Palette Default = new()
    {
        Primary = Color.FromRgb(0x2B, 0x2F, 0x38),
        Secondary = Color.FromRgb(0x1B, 0x1E, 0x25),
        Accent = Color.FromRgb(0xC9, 0xB2, 0x8A)
    };

    public static Palette From(BitmapSource src)
    {
        try
        {
            // Work on a tiny 24x24 copy: cheap, and the result is cached per album.
            double sx = 24.0 / Math.Max(1, src.PixelWidth), sy = 24.0 / Math.Max(1, src.PixelHeight);
            BitmapSource small = new TransformedBitmap(src, new ScaleTransform(sx, sy));
            small = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = small.PixelWidth, h = small.PixelHeight;
            var px = new byte[w * h * 4];
            small.CopyPixels(px, w * 4, 0);

            var buckets = new Dictionary<int, double[]>();   // weight, r, g, b
            for (int i = 0; i < px.Length; i += 4)
            {
                if (px[i + 3] < 128) continue;
                byte b = px[i], g = px[i + 1], r = px[i + 2];
                var (_, s, l) = ToHsl(Color.FromRgb(r, g, b));
                double wt = 1 + 3 * s * (1 - Math.Abs(2 * l - 1));       // favour colourful, mid-lightness pixels
                int key = ((r >> 5) << 6) | ((g >> 5) << 3) | (b >> 5);
                if (!buckets.TryGetValue(key, out var a)) buckets[key] = a = new double[4];
                a[0] += wt; a[1] += r * wt; a[2] += g * wt; a[3] += b * wt;
            }
            if (buckets.Count == 0) return Default;

            var colors = buckets.Values.OrderByDescending(a => a[0])
                .Select(a => Color.FromRgb((byte)(a[1] / a[0]), (byte)(a[2] / a[0]), (byte)(a[3] / a[0]))).ToList();

            Color p = colors[0];
            double ph = ToHsl(p).h;
            Color sec = colors.Skip(1).FirstOrDefault(c => { var t = ToHsl(c); return HueDist(t.h, ph) > 35 && t.s > 0.12; });
            if (sec.A == 0) sec = p;                                         // default(Color) => none found
            Color acc = colors.OrderByDescending(c => { var t = ToHsl(c); return t.s * (1 - Math.Abs(2 * t.l - 1)); }).First();

            var primary = Tune(p, 0.36, 0.17, 0.30);
            var secondary = Tune(sec, 0.30, 0.09, 0.20);
            var accent = Tune(acc, 0.55, 0.60, 0.74, 0.22);

            // contrast guards (light text #F2F2F4 on the background, accent on the background)
            var text = Color.FromRgb(0xF2, 0xF2, 0xF4);
            for (int i = 0; i < 12 && Contrast(primary, text) < 7; i++) primary = Shift(primary, -0.02);
            for (int i = 0; i < 12 && Contrast(accent, primary) < 3; i++) accent = Shift(accent, 0.03);

            return new Palette { Primary = primary, Secondary = secondary, Accent = accent };
        }
        catch { return Default; }
    }

    static Color Tune(Color c, double maxS, double minL, double maxL, double minS = 0)
    {
        var (h, s, l) = ToHsl(c);
        s = s > 0.08 ? Math.Clamp(s, minS, maxS) : Math.Min(s, maxS);   // keep greys grey
        l = Math.Clamp(l, minL, maxL);
        return FromHsl(h, s, l);
    }

    static Color Shift(Color c, double dl)
    {
        var (h, s, l) = ToHsl(c);
        return FromHsl(h, s, Math.Clamp(l + dl, 0.02, 0.96));
    }

    static double HueDist(double a, double b) { double d = Math.Abs(a - b) % 360; return d > 180 ? 360 - d : d; }

    static (double h, double s, double l) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
        double l = (mx + mn) / 2, h = 0, s = 0, d = mx - mn;
        if (d > 1e-9)
        {
            s = l > 0.5 ? d / (2 - mx - mn) : d / (mx + mn);
            if (mx == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (mx == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
        }
        return (h, s, l);
    }

    static Color FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = l - c / 2;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    static double Lum(Color c)
    {
        static double F(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * F(c.R) + 0.7152 * F(c.G) + 0.0722 * F(c.B);
    }

    static double Contrast(Color a, Color b)
    {
        double x = Lum(a), y = Lum(b);
        if (x < y) (x, y) = (y, x);
        return (x + 0.05) / (y + 0.05);
    }
}

/// <summary>Loads embedded / folder artwork once per album and caches artwork + palette (no per-frame processing).</summary>
public static class ArtworkService
{
    static readonly Dictionary<string, (BitmapSource art, Palette pal)> Cache = new();
    static readonly Queue<string> Order = new();
    static readonly string[] FolderNames = { "cover", "folder", "front", "album", "artwork" };
    static readonly string[] FolderExt = { ".jpg", ".jpeg", ".png" };

    public static (BitmapSource art, Palette pal) Get(Track t)
    {
        if (t.IsOnline)                                    // online preview: the cover was downloaded when the track was created
            return (t.OnlineArt, t.OnlineArt != null ? Palette.From(t.OnlineArt) : Palette.Default);
        string dir = Path.GetDirectoryName(t.FilePath) ?? "";
        bool unknownAlbum = string.IsNullOrEmpty(t.Album) || t.Album == "Unknown Album";
        string custom = t.CoverFile;
        bool hasCustom = custom != null && File.Exists(custom);
        string key = hasCustom ? "cover|" + t.FilePath : unknownAlbum ? t.FilePath : dir + "|" + Cmp.Key(t.Album);
        if (Cache.TryGetValue(key, out var hit)) return hit;

        BitmapSource art = null;
        try
        {
            if (hasCustom) art = Decode(bi => bi.UriSource = new Uri(custom));
            art ??= Embedded(t.FilePath) ?? FromFolder(dir);
        }
        catch { }
        (BitmapSource art, Palette pal) res = (art, art != null ? Palette.From(art) : Palette.Default);

        Cache[key] = res;
        Order.Enqueue(key);
        while (Order.Count > 40) Cache.Remove(Order.Dequeue());
        return res;
    }

    /// <summary>Drops cached artwork so a changed cover is read again.</summary>
    public static void Invalidate() { Cache.Clear(); Order.Clear(); }

    static BitmapSource Embedded(string path)
    {
        using var f = TagLib.File.Create(path);
        var pic = f.Tag.Pictures.FirstOrDefault();
        if (pic == null) return null;
        using var ms = new MemoryStream(pic.Data.Data);
        return Decode(bi => bi.StreamSource = ms);
    }

    static BitmapSource FromFolder(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        foreach (var n in FolderNames)
            foreach (var e in FolderExt)
            {
                string p = Path.Combine(dir, n + e);
                if (File.Exists(p)) return Decode(bi => bi.UriSource = new Uri(p));
            }
        return null;
    }

    static BitmapSource Decode(Action<BitmapImage> source)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.DecodePixelWidth = 512;
        source(bi);
        bi.EndInit();
        bi.Freeze();
        return bi;
    }
}
