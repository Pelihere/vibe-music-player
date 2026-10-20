using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PortablePlayer;

/// <summary>
/// The Lyrics page: timed lyrics follow the song (the current line is highlighted and kept in the middle, a click on a line jumps there);
/// plain lyrics are shown as text. Sources and order are described in <see cref="LyricsService"/>.
/// </summary>
public partial class MainWindow
{
    readonly DispatcherTimer _lyrTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    LyricsDoc _lyrDoc;
    Track _lyrTrack;
    CancellationTokenSource _lyrCts;
    readonly List<Border> _lyrRows = new();
    int _lyrActive = -1;
    DateTime _lyrUserScroll = DateTime.MinValue;

    void InitLyrics()
    {
        _lyrTimer.Tick += (_, _) => TickLyrics();
        PageLyrics.IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) _lyrTimer.Start(); else _lyrTimer.Stop(); };
        LyricsScroll.PreviewMouseWheel += (_, _) => _lyrUserScroll = DateTime.UtcNow;
        BtnLyricsOnline.Click += async (_, _) => await LyricsOnline();
        BtnLyricsImport.Click += (_, _) => ImportLyrics();
        BtnLyricsRemove.Click += (_, _) => RemoveMyLyrics();
    }

    void LyricsMessage(string text)
    {
        LyricsHost.Children.Clear();
        _lyrRows.Clear();
        _lyrActive = -1;
        LyricsEmpty.Text = text;
        LyricsEmpty.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Called when the page opens and whenever the song changes while it is open.</summary>
    async void OpenLyrics()
    {
        var t = C.Current;
        _lyrCts?.Cancel();
        _lyrDoc = null;
        _lyrTrack = t;
        BtnLyricsOnline.Visibility = t == null ? Visibility.Collapsed : Visibility.Visible;
        BtnLyricsImport.Visibility = t == null ? Visibility.Collapsed : Visibility.Visible;
        BtnLyricsRemove.Visibility = Visibility.Collapsed;
        if (t == null)
        {
            LyricsSub.Text = "";
            LyricsMessage("Nothing is playing.\nStart a song and its lyrics appear here.");
            return;
        }
        LyricsSub.Text = t.Title + " · " + t.Artist;

        var local = LyricsService.FindLocal(t);
        if (local != null) { ShowLyrics(t, local); return; }

        if (!Remote.Enabled)
        {
            LyricsMessage("No lyrics found for this song.\nPut a .lrc or .txt file with the same name next to it, import one with the button above, or switch on online info in Settings to look it up automatically.");
            return;
        }

        var cts = _lyrCts = new CancellationTokenSource();
        LyricsMessage("Looking for lyrics online…");
        try
        {
            var doc = await LyricsService.FindOnlineAsync(t, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(t, _lyrTrack)) return;
            if (doc != null) ShowLyrics(t, doc);
            else LyricsMessage("No lyrics found for this song.\nYou can import a .lrc or .txt file with the button above.");
        }
        catch (OperationCanceledException) { }
        catch (RemoteException ex)
        {
            if (cts.IsCancellationRequested || !ReferenceEquals(t, _lyrTrack)) return;
            LyricsMessage("Lyrics could not be loaded: " + ex.Describe("lyrics") + "\nTry \"Find online\" again later, or import a file.");
        }
        catch (Exception)
        {
            if (cts.IsCancellationRequested || !ReferenceEquals(t, _lyrTrack)) return;
            LyricsMessage("Lyrics could not be loaded.");
        }
    }

    TextBlock LyricText(string text, double size, string colorKey, FontWeight weight)
    {
        var tb = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        tb.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
        return tb;
    }

    void ShowLyrics(Track t, LyricsDoc doc)
    {
        _lyrDoc = doc;
        LyricsMessage("");
        LyricsSub.Text = t.Title + " · " + t.Artist + "  ·  " + doc.Source + (doc.Synced ? "  ·  synced" : "");
        BtnLyricsRemove.Visibility = doc.Source == "Saved by you" ? Visibility.Visible : Visibility.Collapsed;

        if (doc.Instrumental) { LyricsMessage("This song is instrumental."); return; }

        if (doc.Synced)
        {
            for (int i = 0; i < doc.Lines.Count; i++)
            {
                int idx = i;
                string txt = doc.Lines[i].Text;
                var row = new Border
                {
                    Child = LyricText(string.IsNullOrWhiteSpace(txt) ? "♪" : txt, 22, "Muted", FontWeights.SemiBold),
                    Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 2, 0, 2),
                    BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Background = Brushes.Transparent,
                    Cursor = Cursors.Hand, ToolTip = "Jump to this line"
                };
                row.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
                row.MouseLeftButtonUp += (_, _) => SeekLyric(idx);
                _lyrRows.Add(row);
                LyricsHost.Children.Add(row);
            }
            LyricsScroll.ScrollToVerticalOffset(0);
            TickLyrics();
        }
        else
        {
            var stanzas = doc.Plain.Replace("\r", "").Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var s in stanzas)
            {
                var tb = LyricText(s.Trim('\n'), 18, "Fg", FontWeights.Normal);
                tb.LineHeight = 28;
                tb.Margin = new Thickness(0, 0, 0, 22);
                LyricsHost.Children.Add(tb);
            }
            LyricsScroll.ScrollToVerticalOffset(0);
        }
    }

    void SeekLyric(int i)
    {
        if (_lyrDoc == null || i < 0 || i >= _lyrDoc.Lines.Count) return;
        double dur = C.Duration.TotalSeconds;
        if (dur <= 0) return;
        C.Seek(_lyrDoc.Lines[i].Time / dur);
        _lyrUserScroll = DateTime.MinValue;
        TickLyrics();
    }

    void TickLyrics()
    {
        if (_lyrDoc is not { Synced: true } || _lyrRows.Count != _lyrDoc.Lines.Count) return;
        if (!IsVisible || WindowState == WindowState.Minimized) return;
        if (C.Current == null || !ReferenceEquals(C.Current, _lyrTrack)) return;

        double pos = C.Position.TotalSeconds + 0.2;          // lines should appear a hair before they are sung
        var lines = _lyrDoc.Lines;
        int lo = 0, hi = lines.Count - 1, idx = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (lines[mid].Time <= pos) { idx = mid; lo = mid + 1; } else hi = mid - 1;
        }
        if (idx == _lyrActive) return;

        if (_lyrActive >= 0 && _lyrActive < _lyrRows.Count) StyleLyricRow(_lyrRows[_lyrActive], false);
        _lyrActive = idx;
        if (idx < 0) return;
        var row = _lyrRows[idx];
        StyleLyricRow(row, true);

        if (DateTime.UtcNow - _lyrUserScroll < TimeSpan.FromSeconds(4)) return;      // the user is reading elsewhere
        try
        {
            var p = row.TranslatePoint(new Point(0, 0), LyricsHost);
            double target = p.Y + row.ActualHeight / 2 - LyricsScroll.ViewportHeight / 2;
            LyricsScroll.ScrollToVerticalOffset(Math.Max(0, target));
        }
        catch { }
    }

    static void StyleLyricRow(Border row, bool active)
    {
        var tb = (TextBlock)row.Child;
        if (active)
        {
            row.SetResourceReference(Border.BackgroundProperty, "Accent");
            row.SetResourceReference(Border.BorderBrushProperty, "Border");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "OnAccent");
        }
        else
        {
            row.Background = Brushes.Transparent;
            row.BorderBrush = Brushes.Transparent;
            tb.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        }
    }

    async System.Threading.Tasks.Task LyricsOnline()
    {
        var t = C.Current;
        if (t == null) return;
        if (!Remote.Enabled) { Toast("!", "Online info is switched off in Settings."); return; }
        _lyrCts?.Cancel();
        var cts = _lyrCts = new CancellationTokenSource();
        LyricsService.ForgetCache(t);
        LyricsSub.Text = t.Title + " · " + t.Artist + "  ·  looking online…";
        try
        {
            var doc = await LyricsService.FindOnlineAsync(t, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(t, C.Current)) return;
            if (doc == null) { Toast("!", "No lyrics found online for this song."); LyricsSub.Text = t.Title + " · " + t.Artist; return; }
            _lyrTrack = t;
            ShowLyrics(t, doc);
        }
        catch (OperationCanceledException) { }
        catch (RemoteException ex) { Toast("!", "Lyrics: " + ex.Describe("lyrics")); LyricsSub.Text = t.Title + " · " + t.Artist; }
        catch (Exception) { Toast("!", "Lyrics could not be loaded."); LyricsSub.Text = t.Title + " · " + t.Artist; }
    }

    void ImportLyrics()
    {
        var t = C.Current;
        if (t == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose lyrics for " + t.Title, Filter = "Lyrics (*.lrc;*.txt)|*.lrc;*.txt|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var doc = LyricsService.Import(t, dlg.FileName);
            if (doc == null) { Toast("!", "That file contains no lyrics."); return; }
            _lyrCts?.Cancel();
            _lyrTrack = t;
            ShowLyrics(t, doc);
            Toast("✓", "Lyrics saved for this song.");
        }
        catch (Exception ex) { Toast("!", "Could not read the file: " + ex.Message); }
    }

    void RemoveMyLyrics()
    {
        var t = C.Current;
        if (t == null) return;
        if (!Dialogs.Confirm(this, "Remove your lyrics?", "The lyrics you imported for this song are removed from Vibe. Your original file is not touched.", "Remove", DialogKind.Danger)) return;
        LyricsService.RemoveSaved(t);
        OpenLyrics();
    }
}
