using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PortablePlayer;

/// <summary>
/// The Statistics page: big colour tiles for the numbers that matter, then small cards with bar charts
/// (most played, top artists, genres, library growth, decades, recently played). Rows are clickable.
/// Everything is drawn from theme brushes, so it follows the selected theme.
/// </summary>
public partial class MainWindow
{
    static readonly string[] ToneKeys = { "Accent", "Tone3", "Highlight", "Tone4" };

    static string Ago(DateTime when)
    {
        var d = DateTime.Now - when;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 2) return "yesterday";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays} days ago";
        return when.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }

    /// <summary>A neo-brutalist card: filled with a theme brush, bold outline, hard shadow, fine cross-hatch.</summary>
    Border StatCard(UIElement content, string fillKey, Thickness pad)
    {
        var card = new Border { BorderThickness = new Thickness(2), Padding = pad };
        card.SetResourceReference(Border.BackgroundProperty, fillKey);
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.SetResourceReference(Border.BorderThicknessProperty, "BorderThin");
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        card.SetResourceReference(UIElement.EffectProperty, "CardShadow");
        var hatch = new Rectangle { IsHitTestVisible = false };
        hatch.SetResourceReference(Shape.FillProperty, "SurfaceTexture");
        var host = new Grid();
        host.Children.Add(hatch);
        host.Children.Add(content);
        card.Child = host;
        return card;
    }

    FrameworkElement StatTile(string glyph, string value, string label, string fillKey)
    {
        var g = new Grid { MinHeight = 74 };
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var v = Txt(value, 28, "OnAccent", FontWeights.ExtraBold);
        v.TextTrimming = TextTrimming.CharacterEllipsis; v.TextWrapping = TextWrapping.NoWrap;
        var l = Txt(label.ToUpperInvariant(), 11, "OnAccent", FontWeights.Bold, new Thickness(0, 2, 0, 0));
        l.Opacity = 0.8;
        Grid.SetRow(l, 1);
        var gl = Txt(glyph, 18, "OnAccent", FontWeights.Bold);
        gl.HorizontalAlignment = HorizontalAlignment.Right; gl.VerticalAlignment = VerticalAlignment.Top; gl.Opacity = 0.75;
        g.Children.Add(v); g.Children.Add(l); g.Children.Add(gl);
        var tile = StatCard(g, fillKey, new Thickness(16, 12, 16, 12));
        tile.Width = 178; tile.Margin = new Thickness(0, 0, 18, 18);
        return tile;
    }

    /// <summary>A card with a title and a body (rows / chart).</summary>
    FrameworkElement StatPanel(string title, string sub, string toneKey, UIElement body)
    {
        var inner = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        var chip = new Border { Width = 12, Height = 12, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(2) };
        chip.SetResourceReference(Border.BackgroundProperty, toneKey);
        chip.SetResourceReference(Border.BorderBrushProperty, "Border");
        head.Children.Add(chip);
        head.Children.Add(Txt(title, 15, "Fg", FontWeights.Bold));
        inner.Children.Add(head);
        inner.Children.Add(Txt(sub, 11.5, "Muted", null, new Thickness(21, 0, 0, 14)));
        inner.Children.Add(body);
        var card = StatCard(inner, "Surface", new Thickness(18, 16, 18, 8));
        card.Width = 440; card.Margin = new Thickness(0, 0, 20, 20);
        card.VerticalAlignment = VerticalAlignment.Top;
        return card;
    }

    /// <summary>Label + value on one line, a bold-outlined bar below. Optional click action.</summary>
    FrameworkElement BarRow(string label, string sub, string value, double frac, string fillKey, Action click)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 11), Background = Brushes.Transparent, Cursor = click != null ? Cursors.Hand : Cursors.Arrow };
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Margin = new Thickness(0, 0, 12, 5) };
        var lt = Txt(label, 13, "Fg", FontWeights.SemiBold);
        lt.TextTrimming = TextTrimming.CharacterEllipsis; lt.TextWrapping = TextWrapping.NoWrap;
        left.Children.Add(lt);
        if (!string.IsNullOrEmpty(sub))
        {
            var st = Txt(sub, 11, "Muted");
            st.TextTrimming = TextTrimming.CharacterEllipsis; st.TextWrapping = TextWrapping.NoWrap;
            left.Children.Add(st);
        }
        var val = Txt(value, 12.5, "Fg", FontWeights.Bold);
        val.VerticalAlignment = VerticalAlignment.Top; val.Margin = new Thickness(0, 1, 0, 0);
        Grid.SetColumn(val, 1);
        top.Children.Add(left); top.Children.Add(val);
        row.Children.Add(top);

        frac = Math.Clamp(frac, 0.03, 1);
        var track = new Border { Height = 14, BorderThickness = new Thickness(2) };
        track.SetResourceReference(Border.BackgroundProperty, "Bg");
        track.SetResourceReference(Border.BorderBrushProperty, "Border");
        track.SetResourceReference(Border.BorderThicknessProperty, "BorderThin");
        track.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        var bar = new Grid();
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(frac, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - frac, GridUnitType.Star) });
        var fill = new Border { BorderThickness = new Thickness(0, 0, 2, 0) };
        fill.SetResourceReference(Border.BackgroundProperty, fillKey);
        fill.SetResourceReference(Border.BorderBrushProperty, "Border");
        bar.Children.Add(fill);
        track.Child = bar;
        Grid.SetRow(track, 1);
        row.Children.Add(track);

        if (click != null)
        {
            var sl = new ScoreLayer { Host = row };
            Grid.SetRowSpan(sl, 2);
            row.Children.Insert(0, sl);
            row.MouseEnter += (_, _) => row.SetResourceReference(Panel.BackgroundProperty, "Hover");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, _) => click();
        }
        return row;
    }

    /// <summary>Plain list row (no bar): title, subtitle, small text on the right.</summary>
    FrameworkElement ListRow(string title, string sub, string right, Action click)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 10), Background = Brushes.Transparent, Cursor = click != null ? Cursors.Hand : Cursors.Arrow };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var t = Txt(title, 13, "Fg", FontWeights.SemiBold); t.TextTrimming = TextTrimming.CharacterEllipsis; t.TextWrapping = TextWrapping.NoWrap;
        var s = Txt(sub, 11, "Muted"); s.TextTrimming = TextTrimming.CharacterEllipsis; s.TextWrapping = TextWrapping.NoWrap;
        left.Children.Add(t); left.Children.Add(s);
        var r = Txt(right, 11.5, "Muted"); r.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(r, 1);
        g.Children.Add(left); g.Children.Add(r);
        if (click != null)
        {
            var sl = new ScoreLayer { Host = g };
            Grid.SetColumnSpan(sl, 2);
            g.Children.Insert(0, sl);
            g.MouseEnter += (_, _) => g.SetResourceReference(Panel.BackgroundProperty, "Hover");
            g.MouseLeave += (_, _) => g.Background = Brushes.Transparent;
            g.MouseLeftButtonUp += (_, _) => click();
        }
        return g;
    }

    void RenderStats()
    {
        StatsHost.Children.Clear();
        StatsHost.Children.Add(Txt("Your music", 30, "Fg", FontWeights.ExtraBold));

        var tr = L.Tracks.ToList();
        if (tr.Count == 0)
        {
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(10, 14, 10, 14) };
            var big = Txt("♪", 44, "Accent", FontWeights.Bold); big.HorizontalAlignment = HorizontalAlignment.Center;
            var msg = Txt("Nothing to show yet", 18, "Fg", FontWeights.Bold, new Thickness(0, 4, 0, 4)); msg.HorizontalAlignment = HorizontalAlignment.Center;
            var how = Txt("Add a music folder or drop files onto this window. Play a few songs and this page turns into your listening habits.", 13, "Muted");
            how.TextAlignment = TextAlignment.Center; how.MaxWidth = 380;
            var add = Btn("＋   Add music folder", () => _ = AddFolder(), true);
            add.HorizontalAlignment = HorizontalAlignment.Center; add.Margin = new Thickness(0, 16, 0, 0);
            empty.Children.Add(big); empty.Children.Add(msg); empty.Children.Add(how); empty.Children.Add(add);
            var card = StatCard(empty, "Surface", new Thickness(24));
            card.Margin = new Thickness(0, 22, 18, 18); card.HorizontalAlignment = HorizontalAlignment.Left; card.Width = 460;
            StatsHost.Children.Add(card);
            return;
        }

        int plays = tr.Sum(t => t.PlayCount);
        var lastSong = tr.Where(t => t.LastPlayed != null).OrderByDescending(t => t.LastPlayed).FirstOrDefault();
        StatsHost.Children.Add(Txt(lastSong == null
                ? "Nothing played yet. Start a song and the charts below fill up."
                : $"Last played: {lastSong.Title} - {lastSong.Artist}, {Ago(lastSong.LastPlayed.Value)}",
            12.5, "Muted", null, new Thickness(2, 3, 0, 18)));

        // ---- tiles
        int artists = tr.Select(t => Cmp.Key(t.Artist)).Distinct().Count();
        int albums = tr.Select(t => Cmp.Key(t.Album)).Distinct().Count();
        var tiles = new WrapPanel();
        tiles.Children.Add(StatTile("♫", tr.Count.ToString("N0"), "Songs", "Accent"));
        tiles.Children.Add(StatTile("◔", Duration(tr.Sum(t => t.DurationSec)), "Library length", "Tone3"));
        tiles.Children.Add(StatTile("♪", artists.ToString("N0"), "Artists", "Highlight"));
        tiles.Children.Add(StatTile("◉", albums.ToString("N0"), "Albums", "Tone4"));
        tiles.Children.Add(StatTile("♥", tr.Count(t => t.IsFavorite).ToString("N0"), "Favorites", "Tone3"));
        tiles.Children.Add(StatTile("▶", plays.ToString("N0"), "Total plays", "Accent"));
        StatsHost.Children.Add(tiles);

        var cards = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };

        // ---- most played
        var top = tr.Where(t => t.PlayCount > 0).OrderByDescending(t => t.PlayCount).ThenBy(t => t.Title, Cmp.Ic).Take(8).ToList();
        var topBody = new StackPanel();
        if (top.Count == 0) topBody.Children.Add(Txt("Nothing played yet.", 12.5, "Muted", null, new Thickness(0, 0, 0, 12)));
        for (int i = 0; i < top.Count; i++)
        {
            var t = top[i]; int idx = i;
            topBody.Children.Add(BarRow($"{i + 1}.  {t.Title}", t.Artist, $"{t.PlayCount}×", (double)t.PlayCount / top[0].PlayCount, "Accent",
                () => C.PlayTracks(top.ToList(), idx, null)));
        }
        cards.Children.Add(StatPanel("Most played", "Click a song to play it", "Accent", topBody));

        // ---- top artists
        var topArtists = tr.Where(t => t.PlayCount > 0).GroupBy(t => Cmp.Key(t.Artist))
            .Select(g => (Name: g.First().Artist, N: g.Sum(t => t.PlayCount), Songs: g.Count()))
            .OrderByDescending(x => x.N).Take(6).ToList();
        var artBody = new StackPanel();
        if (topArtists.Count == 0) artBody.Children.Add(Txt("Nothing played yet.", 12.5, "Muted", null, new Thickness(0, 0, 0, 12)));
        foreach (var a in topArtists)
        {
            string name = a.Name;
            artBody.Children.Add(BarRow(a.Name, $"{a.Songs} song{(a.Songs == 1 ? "" : "s")} in your library", $"{a.N}×",
                (double)a.N / topArtists[0].N, "Highlight", () => OpenArtist(name)));
        }
        cards.Children.Add(StatPanel("Top artists", "Click an artist to open the profile", "Highlight", artBody));

        // ---- genres
        var genres = tr.GroupBy(GenreOf, Cmp.Ic).Select(g => (Name: g.Key, N: g.Count())).OrderByDescending(x => x.N).Take(6).ToList();
        var genBody = new StackPanel();
        foreach (var g in genres)
            genBody.Children.Add(BarRow(g.Name, "", $"{g.N}  ·  {Math.Round(100.0 * g.N / tr.Count)}%", (double)g.N / genres[0].N, "Tone3", null));
        cards.Children.Add(StatPanel("Genres", "Share of your library", "Tone3", genBody));

        // ---- decades
        var decades = tr.Where(t => t.Year > 1900).GroupBy(t => t.Year / 10 * 10).Select(g => (D: g.Key, N: g.Count())).OrderByDescending(x => x.N).Take(6).OrderBy(x => x.D).ToList();
        if (decades.Count > 0)
        {
            var decBody = new StackPanel();
            int max = decades.Max(x => x.N);
            foreach (var d in decades) decBody.Children.Add(BarRow($"{d.D}s", "", d.N.ToString(), (double)d.N / max, "Tone4", null));
            cards.Children.Add(StatPanel("Decades", "Songs by release year", "Tone4", decBody));
        }

        // ---- library growth (last 6 months)
        {
            var now = DateTime.Now;
            var months = Enumerable.Range(0, 6).Select(i => new DateTime(now.Year, now.Month, 1).AddMonths(-5 + i)).ToList();
            var counts = months.Select(mo => tr.Count(t => t.Added.ToLocalTime().Year == mo.Year && t.Added.ToLocalTime().Month == mo.Month)).ToList();
            int max = Math.Max(1, counts.Max());
            var grid = new Grid { Height = 150, Margin = new Thickness(0, 0, 0, 10) };
            for (int i = 0; i < months.Count; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var col = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(5, 0, 5, 0) };
                var n = Txt(counts[i].ToString(), 11.5, "Fg", FontWeights.Bold); n.HorizontalAlignment = HorizontalAlignment.Center;
                var bar = new Border { Height = Math.Max(4, 96.0 * counts[i] / max), BorderThickness = new Thickness(2) };
                bar.SetResourceReference(Border.BackgroundProperty, ToneKeys[i % ToneKeys.Length]);
                bar.SetResourceReference(Border.BorderBrushProperty, "Border");
                bar.SetResourceReference(Border.BorderThicknessProperty, "BorderThin");
                bar.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
                var m = Txt(months[i].ToString("MMM", CultureInfo.CurrentCulture), 11, "Muted", null, new Thickness(0, 5, 0, 0)); m.HorizontalAlignment = HorizontalAlignment.Center;
                col.Children.Add(n); col.Children.Add(bar); col.Children.Add(m);
                Grid.SetColumn(col, i);
                grid.Children.Add(col);
            }
            cards.Children.Add(StatPanel("Library growth", "Songs added per month", "Accent", grid));
        }

        // ---- recently played
        var recent = tr.Where(t => t.LastPlayed != null).OrderByDescending(t => t.LastPlayed).Take(7).ToList();
        var recBody = new StackPanel();
        if (recent.Count == 0) recBody.Children.Add(Txt("Nothing played yet.", 12.5, "Muted", null, new Thickness(0, 0, 0, 12)));
        for (int i = 0; i < recent.Count; i++)
        {
            int idx = i;
            recBody.Children.Add(ListRow(recent[i].Title, recent[i].Artist, Ago(recent[i].LastPlayed.Value), () => C.PlayTracks(recent.ToList(), idx, null)));
        }
        cards.Children.Add(StatPanel("Recently played", "Click to play again", "Highlight", recBody));

        StatsHost.Children.Add(cards);
    }
}
