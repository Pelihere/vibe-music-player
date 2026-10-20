using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PortablePlayer;

/// <summary>
/// Settings as a page of the main window (same shell, theme and mini player).
/// Only options the app really supports are listed; every control changes real behaviour.
/// </summary>
public partial class MainWindow
{
    static readonly (string Tag, string Label, string Glyph, string Sub)[] SettingGroups =
    {
        ("General", "General", "⚙", "Start page, closing, online info"),
        ("Playback", "Playback", "▶", "Volume, shuffle, repeat"),
        ("Library", "Library", "♫", "Folders, artist pictures, tags"),
        ("Appearance", "Appearance", "◐", "Themes, motion, density"),
        ("Panel", "Desktop Panel", "▭", "The floating gramophone"),
        ("Hotkeys", "Hotkeys", "⌨", "Global keyboard shortcuts"),
        ("Notifications", "Notifications", "◔", "Toasts and messages"),
        ("Storage", "Storage", "▤", "Caches, logs, data folder"),
        ("About", "About", "ⓘ", "Version, credits, help")
    };

    string _setCat = "General";
    bool _settingsReady;

    void InitSettingsPage()
    {
        SetHost.RenderTransform = new TranslateTransform();
        foreach (var (tag, label, glyph, sub) in SettingGroups)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var gl = new TextBlock { Text = glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            var col = new StackPanel();
            col.Children.Add(new TextBlock { Text = label, FontSize = 13.5, FontWeight = FontWeights.SemiBold });
            col.Children.Add(new TextBlock { Text = sub, FontSize = 10.5, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
            Grid.SetColumn(col, 1);
            grid.Children.Add(gl); grid.Children.Add(col);
            SetNav.Items.Add(new ListBoxItem { Tag = tag, Content = grid, ToolTip = sub });
        }
        SetNav.SelectionChanged += (_, _) =>
        {
            if (SetNav.SelectedItem is ListBoxItem it && _settingsReady) BuildSettings((string)it.Tag, true);
        };
        _settingsReady = true;
    }

    void OpenSettingsPage()
    {
        if (SetNav.SelectedItem == null) SetNav.SelectedIndex = 0;      // builds "General"
        else BuildSettings(_setCat, false);
    }

    // ================================================================== small building blocks
    void Commit(bool hotkeys = false) => A.SettingsChanged(hotkeys);

    TextBlock Txt(string text, double size, string fgKey, FontWeight? weight = null, Thickness? margin = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, fgKey);
        if (weight != null) t.FontWeight = weight.Value;
        if (margin != null) t.Margin = margin.Value;
        return t;
    }

    void SetTitle(string title, string sub)
    {
        SetHost.Children.Add(Txt(title, 30, "Fg", FontWeights.SemiBold));
        SetHost.Children.Add(Txt(sub, 12.5, "Muted", null, new Thickness(2, 3, 0, 8)));
    }

    void Section(string text)
    {
        var g = new Grid { Margin = new Thickness(2, 26, 0, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var bar = new Border { Width = 3, Height = 12, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        bar.SetResourceReference(Border.BackgroundProperty, "AccentGradient");
        var t = Txt(text.ToUpperInvariant(), 11.5, "Accent", FontWeights.SemiBold);
        t.VerticalAlignment = VerticalAlignment.Center;
        var rule = new Border { Height = 1, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 };
        rule.SetResourceReference(Border.BackgroundProperty, "Border");
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(bar); head.Children.Add(t);
        g.Children.Add(head);
        Grid.SetColumn(rule, 1); g.Children.Add(rule);
        SetHost.Children.Add(g);
    }

    /// <summary>One setting as a card: title + explanation on the left, the control on the right. Hover lights it up.</summary>
    void Row(string title, string desc, FrameworkElement control)
    {
        var card = new Border { BorderThickness = new Thickness(2), Padding = new Thickness(18, 13, 18, 13), Margin = new Thickness(0, 0, 6, 10) };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.SetResourceReference(Border.BorderThicknessProperty, "BorderThin");
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        card.SetResourceReference(UIElement.EffectProperty, "CardShadow");
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Margin = new Thickness(0, 0, 28, 0), VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(Txt(title, 14, "Fg", FontWeights.Bold));
        if (!string.IsNullOrEmpty(desc)) left.Children.Add(Txt(desc, 12, "Muted", null, new Thickness(0, 3, 0, 0)));
        g.Children.Add(left);
        if (control != null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            control.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(control, 1);
            g.Children.Add(control);
        }
        var hatch = new Rectangle { IsHitTestVisible = false };
        hatch.SetResourceReference(Shape.FillProperty, "SurfaceTexture");
        var host = new Grid();
        host.Children.Add(hatch);
        host.Children.Add(g);
        card.Child = host;
        SetHost.Children.Add(card);
    }

    CheckBox Switch(bool on, Action<bool> set)
    {
        var c = new CheckBox { Style = (Style)FindResource("ToggleSwitch"), IsChecked = on };
        c.Click += (_, _) => set(c.IsChecked == true);
        return c;
    }

    FrameworkElement Segments(string[] labels, int selected, Action<int> set)
    {
        var panel = new WrapPanel { MaxWidth = 380 };
        string group = Guid.NewGuid().ToString();
        for (int i = 0; i < labels.Length; i++)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Segment"), Content = labels[i], GroupName = group, IsChecked = i == selected };
            int idx = i;
            rb.Checked += (_, _) => set(idx);
            panel.Children.Add(rb);
        }
        return panel;
    }

    FrameworkElement Slider(double value01, Func<double, string> label, Action<double> live, Action<double> done = null)
    {
        var sb = new SeekBar { Width = 200, Value = value01 };
        sb.BindTheme("Accent", "Border", "Fg");
        var lbl = Txt(label(value01), 12.5, "Muted");
        lbl.Width = 48; lbl.TextAlignment = TextAlignment.Right; lbl.VerticalAlignment = VerticalAlignment.Center;
        sb.Dragging += v => { lbl.Text = label(v); live(v); };
        sb.Commit += v => { lbl.Text = label(v); live(v); done?.Invoke(v); };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(sb); row.Children.Add(lbl);
        return row;
    }

    Button Btn(string text, Action click, bool primary = false)
    {
        var b = new Button { Content = text, Style = (Style)FindResource(primary ? "TPrimary" : "TGhost"), Height = 34 };
        b.Click += (_, _) => click();
        return b;
    }

    TextBox Field(string text, double width)
    {
        var t = new TextBox { Style = (Style)FindResource("FieldBox"), Text = text ?? "", Width = width };
        return t;
    }

    static string Size(long bytes) =>
        bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / 1024.0 / 1024.0:0.#} MB";

    // ================================================================== categories
    void BuildSettings(string cat, bool animate)
    {
        _setCat = cat;
        SetHost.Children.Clear();
        switch (cat)
        {
            case "General": BuildGeneral(); break;
            case "Playback": BuildPlayback(); break;
            case "Library": BuildLibrary(); break;
            case "Appearance": BuildAppearance(); break;
            case "Panel": BuildPanel(); break;
            case "Hotkeys": BuildHotkeys(); break;
            case "Notifications": BuildNotifications(); break;
            case "Storage": BuildStorage(); break;
            case "About": BuildAbout(); break;
        }
        if (animate) { SetScroll.ScrollToTop(); Reveal(SetHost); }
    }

    void BuildGeneral()
    {
        SetTitle("General", "How Vibe starts and what it opens on.");
        Section("Startup");
        Row("Start with Windows", "Launch Vibe when you sign in.",
            Switch(S.StartWithWindows, v => { S.StartWithWindows = v; Commit(); }));
        Row("Open the player window at startup", "Off: start quietly in the tray (the desktop panel shows if it is enabled).",
            Switch(S.OpenPlayerOnWindowsStart, v => { S.OpenPlayerOnWindowsStart = v; Commit(); }));
        Section("Opening view");
        var pages = new[] { "Library", "Gramophone", "Favorites", "Artists", "Albums", "Playlists" };
        Row("Default view", "The page Vibe opens on.",
            Segments(pages, Math.Max(0, Array.IndexOf(pages, S.StartPage)), i => { S.StartPage = pages[i]; Commit(); }));
    }

    void BuildPlayback()
    {
        SetTitle("Playback", "These change the player right away.");
        Row("Volume", "Master volume (also saved for the next start).",
            Slider(C.Volume, v => $"{Math.Round(v * 100)}%", v => C.SetVolume(v)));
        Row("Shuffle", "Play the queue in random order.", Switch(C.Shuffle, v => C.SetShuffle(v)));
        Row("Repeat", "Off, repeat the whole queue, or repeat the current song.",
            Segments(new[] { "Off", "All songs", "This song" }, (int)C.Repeat, i => C.SetRepeat((RepeatMode)i)));
    }

    void BuildLibrary()
    {
        SetTitle("Library", "Where your music lives and how Vibe describes it.");
        Section("Music folders");
        foreach (var f in L.Folders.ToList())
        {
            string folder = f;
            Row(folder, "", Btn("Remove", () => { L.RemoveFolder(folder); BuildSettings("Library", false); }));
        }
        if (L.Folders.Count == 0) SetHost.Children.Add(Txt("No folders yet. Songs you drop onto the window are added without a folder.", 12.5, "Muted", null, new Thickness(2, 6, 0, 0)));
        Row("Add music", "Scan another folder for songs.", Btn("＋  Add folder…", () => _ = AddFolderThenRefresh(), true));
        Row("Rescan library", "Pick up new files and drop missing ones.", Btn("Rescan", () => _ = Rescan()));

        Section("Metadata");
        Row("Write edits into the audio files", "When you edit a song, also update the tags inside the file. Edits are always kept in Vibe either way.",
            Switch(S.WriteTagsToFiles, v => { S.WriteTagsToFiles = v; Commit(); if (v) TrackMetadata.FlushPending(); }));

        Section("Online artist info");
        Row("Fetch artist info online", "Pictures, biographies, popular tracks and related artists. Main sources: MusicBrainz, Wikipedia, Deezer, Last.fm (with a key). Fallbacks when one is down: iTunes, TheAudioDB, SoundCloud (with your own credentials).",
            Switch(S.OnlineArtistInfo, v => { S.OnlineArtistInfo = v; Commit(); }));
        var key = Field(S.LastFmApiKey, 270);
        key.LostFocus += (_, _) =>
        {
            string k = key.Text.Trim();
            if (k == S.LastFmApiKey) return;
            S.LastFmApiKey = k;
            Commit();
            Toast("✓", k.Length == 0 ? "Last.fm key removed" : "Last.fm key saved");
        };
        Row("Last.fm API key", "Free (last.fm/api). Needed for Last.fm similar tracks, biographies and top tracks - the best recommendations.", key);

        Section("SoundCloud (optional)");
        var scId = Field(S.SoundCloudClientId, 270);
        var scSecret = Field(S.SoundCloudClientSecret, 270);
        void SaveSc()
        {
            string a = scId.Text.Trim(), b = scSecret.Text.Trim();
            if (a == S.SoundCloudClientId && b == S.SoundCloudClientSecret) return;
            S.SoundCloudClientId = a; S.SoundCloudClientSecret = b;
            Commit();
            Toast("✓", a.Length == 0 || b.Length == 0 ? "SoundCloud not active (needs both values)" : "SoundCloud credentials saved");
        }
        scId.LostFocus += (_, _) => SaveSc();
        scSecret.LostFocus += (_, _) => SaveSc();
        Row("SoundCloud client ID", "From your own app at soundcloud.com/you/apps (SoundCloud approves apps one by one). Used for profile text and public tracks of SoundCloud artists. Audio is never played or downloaded from SoundCloud.", scId);
        Row("SoundCloud client secret", "Stored only in your settings file on this computer.", scSecret);

        Section("Artist pictures");
        int pics = ArtistImageService.Instance.CountPictures();
        Row("Artist pictures folder",
            $"{ArtistImageService.PicturesDir}\nSave a cutout there named after the artist, e.g. “Frank Ocean.png” (more: “Frank Ocean (2).png”, up to 5 rotate). Capitalization and a leading “The” do not matter. This folder is checked first; only artists without a file are looked up online.  Found now: {pics} picture{(pics == 1 ? "" : "s")}.",
            Btn("Open folder", OpenPicturesFolder, true));

        Section("Diagnostics");
        var report = Txt("", 12, "Muted", null, new Thickness(2, 10, 0, 0));
        report.FontFamily = new FontFamily("Consolas");
        var run = Btn("Run test", () => { });
        run.Click += async (_, _) =>
        {
            run.IsEnabled = false;
            report.Text = "Testing…";
            try { report.Text = await RecommendationService.Instance.DiagnoseAsync("Frank Ocean", "Siegfried", CancellationToken.None); }
            catch (Exception ex) { Paths.Log(ex); report.Text = "The test could not run: " + ex.Message; }
            finally { run.IsEnabled = true; }
        };
        Row("Test recommendations", "Runs “Frank Ocean - Siegfried” through every provider and shows what each one answered. No audio provider is involved.", run);
        SetHost.Children.Add(report);
    }

    void OpenPicturesFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ArtistImageService.PicturesDir}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Paths.Log(ex); }
    }

    async Task AddFolderThenRefresh()
    {
        await AddFolder();
        if (_page == "Settings" && _setCat == "Library") BuildSettings("Library", false);
    }

    void BuildAppearance()
    {
        SetTitle("Appearance", "Colour theme (all in the same bold neo-brutalist style), motion and density of the main window.");
        Section("Look");
        SetHost.Children.Add(ThemeGallery());
        Row("Animations", "Full: all motion. Reduced: shorter and simpler. Off: none.",
            Segments(new[] { "Full", "Reduced", "Off" }, (int)S.Animations, i => { S.Animations = (AnimMode)i; Commit(); }));
        var scoreModes = new[] { "Off", "Slow", "Medium", "Fast" };
        Row("Musical hover effect", "Star sparkles and slowly flowing notes fade in behind buttons, tabs and rows while the pointer is on them and fade out when it leaves. Off turns it off, the rest sets the speed. (With Animations Off or Reduced, or when Windows animations are off, it stands still.)",
            Segments(scoreModes, Math.Max(0, Array.IndexOf(scoreModes, S.ScoreEffect)), i => { S.ScoreEffect = scoreModes[i]; Commit(); Toast("♪", $"Musical hover effect: {scoreModes[i]}"); }));
        Row("Row density", "Height of song rows in lists.",
            Segments(new[] { "Comfortable", "Compact" }, S.Density == "Compact" ? 1 : 0, i =>
            {
                S.Density = i == 1 ? "Compact" : "Comfortable";
                ThemeManager.ApplyDensity();
                Commit();
            }));
    }

    /// <summary>Theme picker as colour cards: each shows the real background, surface and accent colours of the theme.</summary>
    FrameworkElement ThemeGallery()
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        Border selectedCard = null;
        Color Col(string hex) => (Color)ColorConverter.ConvertFromString(hex);
        foreach (var t in ThemeManager.All)
        {
            var def = t;
            bool on = ThemeManager.Current == def.Name;
            var card = new Border
            {
                Width = 168, Height = 96, Margin = new Thickness(0, 0, 14, 14), Cursor = Cursors.Hand, BorderThickness = new Thickness(on ? 3 : 2),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 0, ShadowDepth = 4, Direction = 315, Opacity = 1, Color = Colors.Black },
                CornerRadius = new CornerRadius(Math.Max(4, def.RadiusMd)), Background = new SolidColorBrush(Col(def.Bg)), Tag = def.Name,
                BorderBrush = new SolidColorBrush(Col(on ? def.Accent : "#0A0A0A")), ToolTip = $"{def.Name} theme"
            };
            var g = new Grid { Margin = new Thickness(10, 8, 10, 8) };
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var preview = new Border { CornerRadius = new CornerRadius(Math.Max(2, def.RadiusSm)), Background = new SolidColorBrush(Col(def.Surface2)), Margin = new Thickness(0, 0, 0, 6) };
            var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            foreach (var hex in new[] { def.Accent, def.Highlight, def.Tone3, def.Tone4 })
                dots.Children.Add(new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 5, 0), Background = new SolidColorBrush(Col(hex)) });
            var line = new Border { Height = 4, Width = 46, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 8, 0),
                                    VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Col(def.Muted)) };
            var pg = new Grid(); pg.Children.Add(dots); pg.Children.Add(line);
            preview.Child = pg;
            var label = new TextBlock { Text = (on ? "✓  " : def.Glyph + "  ") + def.Name, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Col(def.Fg)), TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetRow(label, 1);
            g.Children.Add(preview); g.Children.Add(label);
            card.Child = g;
            if (on) selectedCard = card;
            card.MouseLeftButtonUp += (_, _) =>
            {
                if (ThemeManager.Current == def.Name) return;
                A.SetTheme(def.Name);
                BuildSettings("Appearance", false);                 // redraw so the check mark / outline move
                Toast("◐", $"Theme: {def.Name}");
            };
            wrap.Children.Add(card);
        }
        _ = selectedCard;
        return wrap;
    }

    void BuildPanel()
    {
        SetTitle("Desktop Panel", "The floating Gramophone that shows while the player window is hidden in the tray.");
        Row("Show the desktop panel", "Appears when the player window is closed to the tray.",
            Switch(S.PanelEnabled, v => { S.PanelEnabled = v; Commit(); }));
        Section("Placement");
        Row("Dock", "Left and right use a tall layout, top uses a wide one.",
            Segments(new[] { "Left", "Right", "Top" }, (int)S.Dock, i => { S.Dock = (PanelDock)i; Commit(); }));
        Row("Size", "", Segments(new[] { "Compact", "Normal", "Large" }, (int)S.Size, i => { S.Size = (PanelSize)i; Commit(); }));
        Row("Opacity", "20% - 100%.",
            Slider((S.Transparency - 0.2) / 0.8, v => $"{Math.Round((0.2 + 0.8 * v) * 100)}%",
                v => { S.Transparency = Math.Round(0.2 + 0.8 * v, 2); A.PanelWin?.ApplyAppearance(); },
                v => Commit()));
        Row("Lock position", "Stops the panel from being dragged.", Switch(S.Locked, v => { S.Locked = v; Commit(); }));
        Row("Always on top", "", Switch(S.AlwaysOnTop, v => { S.AlwaysOnTop = v; Commit(); }));
        Section("Behaviour");
        Row("Auto-hide", "Fade out when the mouse is away; move to the screen edge to bring it back.",
            Switch(S.AutoHide, v => { S.AutoHide = v; Commit(); }));
        var secs = new[] { 3, 5, 10, 30 };
        Row("Auto-hide delay", "",
            Segments(secs.Select(x => $"{x} s").ToArray(), Array.IndexOf(secs, S.AutoHideSeconds), i => { S.AutoHideSeconds = secs[i]; Commit(); }));
        Row("Colors follow the artwork", "The panel card takes its colors from the album cover.",
            Switch(S.DynamicTheme, v => { S.DynamicTheme = v; Commit(); }));
    }

    void BuildHotkeys()
    {
        SetTitle("Hotkeys", "Global shortcuts that work in any program. Click a box and press a combination (Delete clears it).");
        var status = Txt("", 12.5, "Highlight", null, new Thickness(2, 12, 0, 0));
        string Failed(List<string> f) => f != null && f.Count > 0 ? "Could not register (used by another program or invalid): " + string.Join(", ", f) : "";
        status.Text = Failed(A.HotkeyFailures);

        foreach (var a in HotkeyService.Actions)
        {
            string action = a.Key;
            var tb = Field(S.Hotkeys.TryGetValue(action, out var v) ? v : a.Default, 230);
            tb.IsReadOnly = true;
            tb.PreviewKeyDown += (_, e) =>
            {
                e.Handled = true;
                Key kc = e.Key == Key.System ? e.SystemKey : e.Key;
                var mods = Keyboard.Modifiers;
                if (kc is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None) return;
                if (kc == Key.Tab) { e.Handled = false; return; }
                if (mods == ModifierKeys.None)
                {
                    if (kc is Key.Delete or Key.Back) { tb.Text = ""; S.Hotkeys[action] = ""; status.Text = Failed(A.SettingsChanged(true)); }
                    else status.Text = "A hotkey needs at least one of Ctrl, Alt, Shift or Win.";
                    return;
                }
                string text = HotkeyService.Format(mods, kc);
                tb.Text = text;
                S.Hotkeys[action] = text;
                status.Text = Failed(A.SettingsChanged(true));
            };
            Row(a.Label, "", tb);
        }
        SetHost.Children.Add(status);
        Row("Defaults", "Restore the original shortcuts.", Btn("Reset hotkeys", () =>
        {
            foreach (var a in HotkeyService.Actions) S.Hotkeys[a.Key] = a.Default;
            Commit(true);
            BuildSettings("Hotkeys", false);
        }));
    }

    void BuildNotifications()
    {
        SetTitle("Notifications", "Small messages inside the player window.");
        Row("Toast notifications", "Short confirmations for favorites, queue, playlists and theme changes. They never block playback.",
            Switch(S.Toasts, v => { S.Toasts = v; Commit(); }));
    }

    void BuildStorage()
    {
        SetTitle("Storage", "Cached data Vibe keeps next to the program so it does not have to ask the internet twice.");
        var svc = MusicMetadataService.Instance;
        var (profiles, images) = svc.CacheSize();
        Row("Artist information", $"{Size(profiles)}  -  biographies, popular tracks and related artists (kept for 7 days).",
            Btn("Clear", () => { svc.ClearProfiles(); Toast("✓", "Artist information cache cleared"); BuildSettings("Storage", false); }));
        Row("Artist pictures", $"{Size(images)}  -  downloaded pictures such as related-artist covers (your artist pictures folder is never touched).",
            Btn("Clear", () => { svc.ClearImages(); Toast("✓", "Picture cache cleared"); BuildSettings("Storage", false); }));
        Section("Data");
        Row("Discovery log", "Every online request with its result (API keys are never written). Useful when something does not load.", Btn("Open log", () =>
        {
            try
            {
                if (!System.IO.File.Exists(Remote.LogFile)) { Toast("ℹ", "Nothing logged yet"); return; }
                Process.Start(new ProcessStartInfo(Remote.LogFile) { UseShellExecute = true });
            }
            catch (Exception ex) { Paths.Log(ex); }
        }));
        Row("Data folder", Paths.DataDir, Btn("Open folder", () =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Paths.DataDir}\"") { UseShellExecute = true }); }
            catch (Exception ex) { Paths.Log(ex); }
        }));
    }

    void BuildAbout()
    {
        string ver = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        SetTitle("About Vibe", "A portable desktop music player.");
        Row("Version", $"Vibe {ver}  ·  .NET {Environment.Version}", null);
        Section("Credits");
        SetHost.Children.Add(Txt("Tags and artwork are read and written with TagLib#.\n" +
                                 "Artist data: MusicBrainz (identification), Last.fm (biographies, top tracks, similar artists), " +
                                 "Wikipedia (biography fallback), Deezer (pictures, previews), iTunes Search and TheAudioDB (fallbacks), SoundCloud (optional, own API key).\n" +
                                 "Icons: Segoe Fluent Icons.", 13, "Muted", null, new Thickness(2, 8, 0, 0)));
    }
}
