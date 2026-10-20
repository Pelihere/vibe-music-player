using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace PortablePlayer;

/// <summary>Card (related artist / release) whose picture arrives later; the card never waits for it.</summary>
public sealed class RelatedCard : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;
    ImageSource _image;
    public string Name { get; init; }
    public string Sub { get; init; }
    public string Link { get; init; }
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name.Substring(0, 1).ToUpperInvariant();
    public string ImageUrl { get; set; }
    public ImageSource Image
    {
        get => _image;
        set { _image = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image))); }
    }
}

/// <summary>
/// Artist profile. Local library and online data are two separate sources that are merged here:
/// every song is LOCAL (a file you own, shown with your own metadata) or ONLINE (known from an online service).
/// </summary>
public partial class MainWindow
{
    string _artistName, _artistSeed, _artistReturn, _section = "Overview";
    ArtistProfile _profile;
    // rotating hero (up to five pictures, changed every HeroRotateSeconds, only while the page is visible)
    const int HeroRotateSeconds = 6;
    List<HeroImage> _heroes = new();
    int _heroIndex, _heroGen;
    bool _heroLoaded, _heroBusy;
    Border _heroFront, _heroBack;
    DispatcherTimer _heroTimer;
    readonly Dictionary<string, BitmapSource> _heroBitmaps = new();
    CancellationTokenSource _artistCts;
    readonly Stack<(string Name, string Seed)> _artistBack = new();
    List<PopularItem> _popAll = new(), _recAll = new();
    List<Track> _popTracks = new();                      // local songs in the order shown = the play queue
    readonly Dictionary<string, ImageSource> _coverMemo = new();
    bool _popExpanded, _bioExpanded;
    string _artSort = "Popularity";
    bool _artSortDesc;
    double _heroTargetOpacity = 1;

    void InitArtist()
    {
        BtnArtPlay.Click += (_, _) => PlayArtist(false);
        BtnArtShuffle.Click += (_, _) => PlayArtist(true);
        BtnArtSort.Click += (_, _) => ShowArtistSort();
        BtnArtFav.Click += (_, _) => ToggleArtistFavorite();
        BtnArtMore.Click += (_, _) => ShowArtistMore();
        BtnArtistBack.Click += (_, _) => ArtistBack();
        BtnArtRetry.Click += (_, _) => RetryArtist(false);
        BtnPopMore.Click += (_, _) => { _popExpanded = !_popExpanded; ShowPopular(); };
        BtnReadMore.Click += (_, _) => { _bioExpanded = !_bioExpanded; RenderBio(_profile); };

        NavAbout.Checked += (_, _) => SelectSection("About", true);
        NavOverview.Checked += (_, _) => SelectSection("Overview", true);
        NavRelated.Checked += (_, _) => SelectSection("Related", true);

        foreach (var lb in new[] { PopList, RecList })
        {
            lb.PreviewMouseLeftButtonUp += OnPopClick;
            lb.AddHandler(Button.ClickEvent, new RoutedEventHandler(OnMoreClick));
            lb.PreviewMouseWheel += (_, e) => { ContentScroll.ScrollToVerticalOffset(ContentScroll.VerticalOffset - e.Delta); e.Handled = true; };
            lb.PreviewMouseRightButtonDown += (_, e) => SelectUnderMouse(lb, e);
        }
        PopList.ContextMenuOpening += (_, e) => { if (!FillTrackMenu(PopMenu, PopList, _popTracks, null)) e.Handled = true; };
        RecList.ContextMenuOpening += (_, e) => { if (!FillTrackMenu(RecMenu, RecList, null, null)) e.Handled = true; };
        RelatedItems.AddHandler(Button.ClickEvent, new RoutedEventHandler(OnRelatedClick));
        ReleaseItems.AddHandler(Button.ClickEvent, new RoutedEventHandler(OnReleaseClick));
        PageArtist.SizeChanged += (_, _) => LayoutArtist();

        _heroFront = HeroA; _heroBack = HeroB;
        _heroTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(HeroRotateSeconds) };
        _heroTimer.Tick += (_, _) => NextHero();
        // the rotation lives and dies with the page: hidden / left = timer stopped, pictures released
        PageArtist.IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) ResumeHero(); else StopHero(); };
    }

    // ================================================================== navigation into / out of the profile
    /// <summary>The cover of the playing song (mini player, queue) opens the artist profile - never the Gramophone.</summary>
    void OpenCurrentArtist()
    {
        var t = C.Current;
        if (t == null) { Toast("♪", "Nothing is playing yet"); return; }
        OpenArtist(t.Artist);
    }

    /// <summary>Opens an artist. With a seed song the recommendations are "similar to that song".</summary>
    public void OpenArtist(string name, string seed = null)
    {
        name = Cmp.Norm(name);
        if (string.IsNullOrEmpty(name)) return;
        seed = string.IsNullOrWhiteSpace(seed) ? null : seed.Trim();

        if (_page == "Artist" && _artistName != null)
        {
            if (Cmp.SameArtist(_artistName, name))
            {
                if (seed == _artistSeed) return;
                BeginArtist(name, seed);
                return;
            }
            _artistBack.Push((_artistName, _artistSeed));          // Artist → related artist → Artist … with a way back
        }
        else
        {
            _artistBack.Clear();
            _artistReturn = _navPage;
        }
        BeginArtist(name, seed);
    }

    void ArtistBack()
    {
        if (_artistBack.Count > 0) { var (n, s) = _artistBack.Pop(); BeginArtist(n, s); }
        else Navigate(string.IsNullOrEmpty(_artistReturn) || _artistReturn == "Artist" ? "Library" : _artistReturn);
    }

    void BeginArtist(string name, string seed)
    {
        _artistCts?.Cancel();
        var cts = _artistCts = new CancellationTokenSource();

        _artistName = name; _artistSeed = seed;
        _profile = null;
        StopHero();
        _heroes = new();
        _popExpanded = _bioExpanded = false;
        _artSort = "Popularity"; _artSortDesc = false;
        _popAll = new(); _recAll = new(); _popTracks = new();

        ArtistName.Text = name.ToUpperInvariant();
        ArtistMonogram.Text = name.Substring(0, 1).ToUpperInvariant();
        ArtistMonogram.Visibility = Visibility.Visible;
        ArtistImage.BeginAnimation(OpacityProperty, null);
        ArtistImage.Opacity = 0;
        ArtistImage.Effect = null;
        PopList.ItemsSource = RecList.ItemsSource = RelatedItems.ItemsSource = ReleaseItems.ItemsSource = null;
        BtnArtistBack.Visibility = Visibility.Visible;
        NavOverview.IsChecked = true;
        SelectSection("Overview", false);
        ContentScroll.ScrollToTop();

        ApplyProfile(null);                                     // shell: library parts + loading placeholders at once
        LayoutArtist();

        ClearSearch(false);
        Nav.SelectedItem = null;                                // so a sidebar entry can be clicked again to leave
        _page = _navPage = "Artist";
        Present(PageArtist, true);
        SetMini(true);

        _ = LoadArtistAsync(name, seed, cts, false);
    }

    async Task LoadArtistAsync(string name, string seed, CancellationTokenSource cts, bool refresh)
    {
        if (!Remote.Enabled) return;
        try
        {
            await MusicMetadataService.Instance.GetProfileAsync(name, seed, p =>
            {
                if (ReferenceEquals(cts, _artistCts) && !cts.IsCancellationRequested) ApplyProfile(p);
            }, cts.Token, refresh);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Paths.Log(ex);
            if (ReferenceEquals(cts, _artistCts)) ArtistStatus.Text = Remote.Classify(ex).Message;
        }
    }

    void RetryArtist(bool refresh)
    {
        if (_artistName == null) return;
        _artistCts?.Cancel();
        var cts = _artistCts = new CancellationTokenSource();
        if (refresh) { ArtistImageService.Instance.Forget(_artistName); StopHero(); _heroes = new(); }
        _ = LoadArtistAsync(_artistName, _artistSeed, cts, refresh);
    }

    // ================================================================== library side
    List<Track> ArtistTracks() =>
        _artistName == null ? new List<Track>()
            : L.Tracks.Where(t => Cmp.SameArtist(t.Artist, _artistName) || Cmp.SameArtist(t.AlbumArtist, _artistName)).ToList();

    void RefreshArtistLocal()
    {
        if (_artistName != null) ApplyProfile(_profile);
    }

    static string FormatCount(long n) => n.ToString("N0", CultureInfo.CurrentCulture);

    static string CountryName(string code)
    {
        if (string.IsNullOrEmpty(code)) return "";
        try { return new RegionInfo(code).EnglishName; } catch { return code; }
    }

    static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Paths.Log(ex); }
    }

    // ================================================================== rendering of one profile state (called after every stage)
    static (RemoteState State, string Message) St<T>(Section<T> s, bool online) =>
        !online ? (RemoteState.Disabled, "") :
        s == null || s.State == RemoteState.NotLoaded ? (RemoteState.Loading, "") : (s.State, s.Message);

    void ApplyProfile(ArtistProfile p)
    {
        _profile = p;
        bool online = Remote.Enabled;
        var mine = ArtistTracks();

        RenderMeta(p, mine);
        RenderHero(p, online);
        BuildPopular(p, mine, online);
        RenderRecommended(p, online);
        RenderReleases(p, online);
        RenderBio(p);
        RenderRelated(p, online);
        RenderStatus(p, online);
        UpdateArtistFav();
        BtnArtPlay.IsEnabled = BtnArtShuffle.IsEnabled = _popAll.Count > 0;
        BtnArtSort.IsEnabled = _popAll.Count > 1;
    }

    void RenderMeta(ArtistProfile p, List<Track> mine)
    {
        var info = p?.Info;
        var meta = new List<string>();
        if (!string.IsNullOrEmpty(info?.Type)) meta.Add(info.Type);
        if (!string.IsNullOrEmpty(info?.Country)) meta.Add(CountryName(info.Country));
        if (info?.BeginYear > 0) meta.Add((info.Ended ? "active " : "since ") + info.BeginYear);
        if (info?.Tags.Count > 0) meta.Add(string.Join(", ", info.Tags.Take(3)));
        int albums = mine.Select(t => Cmp.Key(t.Album)).Distinct().Count();
        meta.Add(mine.Count == 0 ? "none of their songs in your library"
            : $"{mine.Count} song{(mine.Count == 1 ? "" : "s")} in your library");
        ArtistMeta.Text = string.Join("  ·  ", meta);
    }

    void RenderStatus(ArtistProfile p, bool online)
    {
        // no picture anywhere (local folder and APIs): say exactly what to do
        string hint = p != null && p.HeroState == RemoteState.Ok && (p.Images?.Count ?? 0) == 0 && _artistName != null
            ? $"\nNo picture yet. Save a cutout as “{ArtistImageService.SuggestedFileName(_artistName)}” in the artist pictures folder (⋯ → Open artist pictures folder)."
            : "";
        if (!online) { ArtistStatus.Text = "Online discovery is switched off (Settings → Library)." + hint; BtnArtRetry.Visibility = Visibility.Collapsed; return; }
        var states = new List<(RemoteState S, string M)>
        {
            (p?.InfoState ?? RemoteState.Loading, p?.InfoMessage ?? ""), St(p?.Top, true), St(p?.Similar, true), St(p?.Recommended, true), St(p?.Releases, true)
        };
        var failed = states.FirstOrDefault(x => x.S == RemoteState.Failed || x.S == RemoteState.Offline);
        bool hasFailure = failed.S == RemoteState.Failed || failed.S == RemoteState.Offline;
        bool loading = p == null || states.Any(x => x.S == RemoteState.Loading || x.S == RemoteState.NotLoaded);
        ArtistStatus.Text = (hasFailure ? failed.M
                          : loading ? "Loading artist info…" : "") + hint;
        BtnArtRetry.Visibility = hasFailure && !loading ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ hero pictures (rotating, crossfaded)
    bool HeroPageActive => PageArtist.IsVisible && WindowState != WindowState.Minimized;

    void RenderHero(ArtistProfile p, bool online)
    {
        var imgs = p?.Images;
        if (imgs == null || imgs.Count == 0) return;
        if (_heroes.Count == imgs.Count && _heroes.Select(i => i.Url).SequenceEqual(imgs.Select(i => i.Url))) return;   // same set: nothing to do
        _heroes = imgs.ToList();
        _heroIndex = 0;
        _ = StartHeroAsync();
    }

    async Task<BitmapSource> HeroBitmap(HeroImage h, CancellationToken ct)
    {
        if (_heroBitmaps.TryGetValue(h.Url, out var hit)) return hit;
        var bmp = await ArtistImageService.Instance.LoadAsync(h.Url, 1600, ct);       // decoded as it is, nothing is processed
        if (bmp != null) _heroBitmaps[h.Url] = bmp;
        return bmp;
    }

    /// <summary>Shows the first picture (from the cache) and starts the timer; the first change happens one interval later, never on open.</summary>
    async Task StartHeroAsync()
    {
        int gen = ++_heroGen;
        _heroTimer.Stop();
        var cts = _artistCts;
        if (cts == null || _heroes.Count == 0 || !HeroPageActive) return;
        try
        {
            int i = 0;
            BitmapSource bmp = null;
            for (; i < _heroes.Count && bmp == null; i++) bmp = await HeroBitmap(_heroes[i], cts.Token);
            i--;                                                       // index of the picture that loaded
            if (bmp == null || gen != _heroGen || cts.IsCancellationRequested) return;

            _heroIndex = i;
            var h = _heroes[i];
            PlaceHero(_heroFront, h, bmp);
            _heroFront.Opacity = 1; _heroBack.Opacity = 0;
            _heroBack.Background = null;
            _heroLoaded = true;
            Panel.SetZIndex(NameBox, h.Kind == "Landscape" ? 3 : 0);
            ArtistMonogram.Visibility = Visibility.Collapsed;
            LayoutArtist();
            AnimateTo(ArtistImage, OpacityProperty, _heroTargetOpacity, S.Animations == AnimMode.Off ? 0 : 520);
            SetHeroFocus(false);
            if (_heroes.Count >= 2) _heroTimer.Start();                // one picture: no rotation, no timer
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Paths.Log(ex); }
    }

    void PlaceHero(Border b, HeroImage h, BitmapSource bmp)
    {
        b.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Top };
        b.OpacityMask = MaskFor(h.Kind);          // a picture that already has transparency needs no mask
        b.Tag = h;
        ApplyHeroLayout(b, h);
    }

    /// <summary>Timer tick: smooth crossfade to the next loadable picture.</summary>
    async void NextHero()
    {
        if (_heroBusy || _heroes.Count < 2 || !HeroPageActive || !_heroLoaded) return;
        _heroBusy = true;
        int gen = _heroGen;
        var cts = _artistCts;
        try
        {
            for (int tries = 1; tries <= _heroes.Count; tries++)
            {
                int idx = (_heroIndex + tries) % _heroes.Count;
                var h = _heroes[idx];
                var bmp = await HeroBitmap(h, cts.Token);
                if (bmp == null) continue;
                if (gen != _heroGen || cts.IsCancellationRequested || !HeroPageActive) return;
                Crossfade(h, bmp);
                _heroIndex = idx;
                break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Paths.Log(ex); }
        finally { _heroBusy = false; }
    }

    void Crossfade(HeroImage h, BitmapSource bmp)
    {
        var front = _heroFront; var back = _heroBack;
        PlaceHero(back, h, bmp);
        Panel.SetZIndex(NameBox, h.Kind == "Landscape" ? 3 : 0);
        LayoutArtist();

        int ms = S.Animations == AnimMode.Off ? 0 : S.Animations == AnimMode.Full ? 1100 : 450;
        front.BeginAnimation(OpacityProperty, null); back.BeginAnimation(OpacityProperty, null);
        if (ms == 0) { back.Opacity = 1; front.Opacity = 0; front.Background = null; _heroFront = back; _heroBack = front; return; }

        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        back.Opacity = 0;
        back.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        int gen = _heroGen;
        fadeOut.Completed += (_, _) =>
        {
            if (gen != _heroGen) return;                          // page left / artist changed meanwhile
            back.Opacity = 1; front.Opacity = 0;
            front.Background = null;                              // release the old picture
        };
        // the incoming picture becomes the front one immediately; its opacity is driven by the animation above
        back.Opacity = 1;
        front.Opacity = 0;
        _heroFront = back; _heroBack = front;
        front.BeginAnimation(OpacityProperty, fadeOut);
    }

    /// <summary>Page left (or window hidden): timer off, pending loads cancelled, pictures released.</summary>
    void StopHero()
    {
        _heroTimer?.Stop();
        _heroGen++;
        _heroBusy = false;
        _heroLoaded = false;
        _heroBitmaps.Clear();
        foreach (var b in new[] { HeroA, HeroB })
        {
            b.BeginAnimation(OpacityProperty, null);
            b.Opacity = 0;
            b.Background = null;
            b.OpacityMask = null;
            b.Tag = null;
        }
        _heroFront = HeroA; _heroBack = HeroB;
    }

    /// <summary>Page shown again for the same artist: pictures come from the cache and the rotation restarts from the first one.</summary>
    void ResumeHero()
    {
        if (_heroes.Count == 0 || _artistName == null) return;
        _heroIndex = 0;
        _ = StartHeroAsync();
    }

    static Brush MaskFor(string kind)
    {
        static Color A(byte a) => Color.FromArgb(a, 0, 0, 0);
        switch (kind)
        {
            case "Cutout": return null;                                       // already isolated from its background
            case "Landscape":
                return new LinearGradientBrush(new GradientStopCollection
                {
                    new GradientStop(A(0), 0.0), new GradientStop(A(255), 0.2), new GradientStop(A(255), 0.8), new GradientStop(A(0), 1.0)
                }, new Point(0, 0), new Point(1, 0));
            default:
                return new RadialGradientBrush(new GradientStopCollection
                {
                    new GradientStop(A(255), 0.0), new GradientStop(A(255), 0.55), new GradientStop(A(0), 1.0)
                })
                { Center = new Point(0.5, 0.42), GradientOrigin = new Point(0.5, 0.42), RadiusX = 0.64, RadiusY = 0.62 };
        }
    }

    /// <summary>Sizes the hero for the current window: wide = nav | portrait | content, narrower = the content slides over the portrait.</summary>
    void LayoutArtist()
    {
        double w = PageArtist.ActualWidth, h = PageArtist.ActualHeight;
        if (w < 100 || h < 100) return;
        bool wide = w >= 1080;

        double leftW = 236;
        double contentW = Math.Min(wide ? 540 : Math.Max(380, w - 270), w - leftW - 16);
        LeftPanel.Width = leftW;
        ContentScroll.Width = Math.Max(300, contentW);
        ContentScroll.Margin = new Thickness(0, Math.Max(140, h * 0.27), 24, 12);

        HeroStack.Height = h;
        NameBox.MaxHeight = Math.Max(90, h * 0.34);
        NameBox.Margin = new Thickness(40, Math.Max(8, h * 0.035), 40, 0);

        foreach (var b2 in new[] { HeroA, HeroB })
            if (b2.Tag is HeroImage hi) ApplyHeroLayout(b2, hi);
        HeroFade.Height = Math.Max(120, h * 0.3);
    }

    /// <summary>Size and position of one hero picture for the current window (nav | picture | content; the content slides over it when narrow).</summary>
    void ApplyHeroLayout(Border b, HeroImage hero)
    {
        double w = PageArtist.ActualWidth, h = PageArtist.ActualHeight;
        if (w < 100 || h < 100 || hero == null) return;
        const double leftW = 236;
        string kind = hero.Kind;
        double aspect = hero.Height > 0 ? (double)hero.Width / hero.Height : 0.8;
        double imgH = kind == "Landscape" ? h * 0.74 : kind == "Cutout" ? h * 0.95 : h * 0.88;
        double imgW = kind switch
        {
            "Landscape" => Math.Min(w * 0.92, imgH * aspect),
            "Square" => imgH,
            _ => Math.Min(imgH * aspect, w * 0.72)
        };
        if (kind != "Landscape" && kind != "Square" && imgW < imgH * aspect) imgH = imgW / aspect;
        b.Height = imgH; b.Width = imgW;
        double free = w - leftW - ContentScroll.Width;
        double center = free >= imgW ? leftW + free / 2 : w * 0.46;
        b.Margin = new Thickness(Math.Max(0, center - imgW / 2), 0, 0, 0);
    }

    // ------------------------------------------------------------------ sections (left navigation)
    void SelectSection(string section, bool animate)
    {
        _section = section;
        UIElement panel = section switch { "About" => PanelAbout, "Related" => PanelRelated, _ => PanelOverview };
        foreach (var p in new UIElement[] { PanelOverview, PanelAbout, PanelRelated })
            if (!ReferenceEquals(p, panel)) p.Visibility = Visibility.Collapsed;
        panel.Visibility = Visibility.Visible;
        ContentScroll.ScrollToTop();
        if (animate && S.Animations != AnimMode.Off)
            panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(S.Animations == AnimMode.Full ? 240 : 110))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else { panel.BeginAnimation(OpacityProperty, null); panel.Opacity = 1; }
        SetHeroFocus(animate);
    }

    static void AnimateTo(IAnimatable el, DependencyProperty dp, double to, int ms)
    {
        el.BeginAnimation(dp, null);
        if (ms <= 0) { ((DependencyObject)el).SetValue(dp, to); return; }
        el.BeginAnimation(dp, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
    }

    /// <summary>OVERVIEW: the portrait is at full strength. ABOUT / RELATED: it recedes (dimmer, softly blurred) so the information leads.</summary>
    void SetHeroFocus(bool animate)
    {
        double opacity = _section switch { "About" => 0.42, "Related" => 0.30, _ => 1.0 };
        double blur = _section switch { "About" => 5, "Related" => 8, _ => 0 };
        double nameOpacity = _section == "Overview" ? 0.94 : 0.5;
        _heroTargetOpacity = opacity;
        int ms = !animate || S.Animations == AnimMode.Off ? 0 : S.Animations == AnimMode.Full ? 420 : 170;

        if (_heroLoaded) AnimateTo(ArtistImage, OpacityProperty, opacity, ms);
        AnimateTo(ArtistName, OpacityProperty, nameOpacity, ms);
        AnimateTo(ArtistMonogram, OpacityProperty, _section == "Overview" ? 0.13 : 0.06, ms);

        if (blur > 0 && ArtistImage.Effect is not BlurEffect) ArtistImage.Effect = new BlurEffect { Radius = 0, KernelType = KernelType.Gaussian };
        if (ArtistImage.Effect is BlurEffect be)
        {
            be.BeginAnimation(BlurEffect.RadiusProperty, null);
            if (ms == 0) { be.Radius = blur; if (blur == 0) ArtistImage.Effect = null; }
            else
            {
                var a = new DoubleAnimation(blur, TimeSpan.FromMilliseconds(ms));
                if (blur == 0) a.Completed += (_, _) => { if (_section == "Overview") ArtistImage.Effect = null; };
                be.BeginAnimation(BlurEffect.RadiusProperty, a);
            }
        }
    }

    // ================================================================== OVERVIEW: popular tracks (local + online merged)
    /// <summary>What can be done with an online song, said honestly: preview, link only, or nothing to play.</summary>
    static string BadgeFor(RemoteTrack r) =>
        r.HasPreview ? "ONLINE · PREVIEW"
        : !r.PreviewChecked ? "ONLINE"
        : !string.IsNullOrEmpty(r.Url) ? "ONLINE · LINK"
        : "ONLINE · NO PLAYBACK SOURCE";

    static string PlaysText(RemoteTrack r) =>
        r.Plays > 0 ? $"{FormatCount(r.Plays)} plays" : r.Score > 0 ? $"{Math.Round(r.Score * 100)}% match" : "";

    PopularItem RemoteItem(RemoteTrack r, int rank) => new()
    {
        Remote = r, Rank = rank,
        Plays = PlaysText(r),
        Badge = BadgeFor(r),
        RemoteThumb = !string.IsNullOrEmpty(r.CoverUrl) && _coverMemo.TryGetValue(r.CoverUrl, out var img) ? img : null
    };

    /// <summary>Compares online songs with the library (case-insensitive) and builds one list. A song you own appears once, as LOCAL.</summary>
    List<PopularItem> Merge(IEnumerable<RemoteTrack> remote, List<Track> mine, bool appendLocalRest)
    {
        var items = new List<PopularItem>();
        var used = new HashSet<Track>();
        var pool = mine ?? L.Tracks.ToList();
        int rank = 0;
        foreach (var r in remote)
        {
            string key = Cmp.TitleKey(r.Title);
            if (key.Length == 0 || items.Any(i => Cmp.TitleKey(i.Title) == key && Cmp.SameArtist(i.Artist, r.Artist))) continue;
            var local = pool.FirstOrDefault(t => !used.Contains(t) && Cmp.TitleKey(t.Title) == key &&
                                                                         (Cmp.SameArtist(t.Artist, r.Artist) || Cmp.SameArtist(t.AlbumArtist, r.Artist)));
            if (local != null)
            {
                used.Add(local);
                items.Add(new PopularItem { Track = local, Remote = r, Rank = ++rank, Badge = "LOCAL", Plays = PlaysText(r) });
            }
            else items.Add(RemoteItem(r, ++rank));
        }
        if (appendLocalRest && mine != null)
            foreach (var t in mine.Where(x => !used.Contains(x)).OrderByDescending(x => x.PlayCount).ThenBy(x => x.Title, Cmp.Ic))
                items.Add(new PopularItem { Track = t, Rank = ++rank, Badge = "LOCAL",
                                            Plays = t.PlayCount > 0 ? $"{t.PlayCount} play{(t.PlayCount == 1 ? "" : "s")}" : "" });
        return items;
    }

    void BuildPopular(ArtistProfile p, List<Track> mine, bool online)
    {
        var (state, message) = St(p?.Top, online);
        _popAll = Merge(state == RemoteState.Ok ? p.Top.Items : Enumerable.Empty<RemoteTrack>(), mine, true);
        ShowPopular();

        SetSkeleton(PopSkeleton, _popAll.Count == 0 ? 5 : 2, state == RemoteState.Loading);
        PopState.Text = state switch
        {
            RemoteState.Failed or RemoteState.Offline => message + (_popAll.Count > 0 ? "  Showing your library only." : ""),
            RemoteState.Disabled => _popAll.Count == 0 ? "Online discovery is switched off. None of this artist's songs are in your library." : "",
            RemoteState.NoData => _popAll.Count == 0 ? "No popular tracks were found for this artist." : "",
            RemoteState.Ok => "",
            _ => _popAll.Count == 0 && state != RemoteState.Loading ? "None of this artist's songs are in your library yet." : ""
        };
    }

    void ShowPopular()
    {
        IEnumerable<PopularItem> sorted = _artSort switch
        {
            "Title" => _artSortDesc ? _popAll.OrderByDescending(p => p.Title, Cmp.Ic) : _popAll.OrderBy(p => p.Title, Cmp.Ic),
            "Album" => _artSortDesc ? _popAll.OrderByDescending(p => p.Album, Cmp.Ic) : _popAll.OrderBy(p => p.Album, Cmp.Ic),
            "Duration" => _artSortDesc ? _popAll.OrderByDescending(Dur) : _popAll.OrderBy(Dur),
            _ => _artSortDesc ? Enumerable.Reverse(_popAll) : _popAll
        };
        var list = sorted.ToList();
        _popTracks = list.Where(i => i.IsLocal).Select(i => i.Track).ToList();     // the play queue follows the order on screen

        var shown = _popExpanded ? list : list.Take(5).ToList();
        PopList.ItemsSource = shown;
        BtnPopMore.Visibility = list.Count > 5 ? Visibility.Visible : Visibility.Collapsed;
        BtnPopMore.Content = _popExpanded ? "Show less" : $"Show more  ({list.Count - 5})";
        BtnArtSort.ToolTip = $"Sort: {_artSort}";
        _ = LoadRemoteThumbs(shown);
        _ = ResolvePlayback(shown);
    }

    static double Dur(PopularItem i) => i.Track != null ? i.Track.DurationSec : i.Remote?.DurationSec ?? 0;

    async Task LoadRemoteThumbs(IEnumerable<PopularItem> items)
    {
        var cts = _artistCts;
        if (cts == null) return;
        foreach (var it in items.Where(i => !i.IsLocal && i.RemoteThumb == null && !string.IsNullOrEmpty(i.Remote?.CoverUrl)).ToList())
        {
            if (cts.IsCancellationRequested) return;
            string url = it.Remote.CoverUrl;
            try
            {
                if (!_coverMemo.TryGetValue(url, out var img))
                {
                    img = await ArtistImageService.Instance.LoadAsync(url, 96, cts.Token);
                    if (img != null) _coverMemo[url] = img;
                }
                if (img != null) it.RemoteThumb = img;
            }
            catch (OperationCanceledException) { return; }
            catch { }
        }
    }

    // ------------------------------------------------------------------ click / play
    void OnPopClick(object sender, MouseButtonEventArgs e)
    {
        if (UiUtil.FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return;
        if (UiUtil.FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not PopularItem pi) return;
        if (pi.IsLocal)
        {
            var list = ReferenceEquals(sender, PopList) ? _popTracks : _recAll.Where(i => i.IsLocal).Select(i => i.Track).ToList();
            int idx = list.IndexOf(pi.Track);
            if (idx >= 0) C.PlayTracks(list.ToList(), idx, null);              // a file you own: the normal player
        }
        else _ = PlayRemote(pi, false);
    }

    /// <summary>
    /// Plays the 30-second preview the (optional) audio provider offers. When there is none, that is said plainly:
    /// it is a statement about audio, never an error about recommendations.
    /// </summary>
    int _previewTicket;
    async Task PlayRemote(PopularItem pi, bool enqueueOnly)
    {
        var r = pi?.Remote;
        if (r == null) return;
        if (r.PreviewChecked && !r.HasPreview) { Toast("♪", "No preview is available for this song."); return; }
        int ticket = ++_previewTicket;                              // the latest click wins; older ones are dropped
        Toast("⟳", $"Loading preview of “{r.Title}”…");
        try
        {
            // nothing about the current playback is touched until the clip is really downloaded
            var tr = await AudioProviderService.Instance.CreatePreviewTrackAsync(r, CancellationToken.None);
            r.PreviewChecked = true;
            pi.Badge = BadgeFor(r);
            if (ticket != _previewTicket && !enqueueOnly) return;
            if (tr == null) { Toast("♪", "No preview is available for this song."); return; }
            if (enqueueOnly) C.Enqueue(new List<Track> { tr });
            else { C.PlayTracks(new List<Track> { tr }, 0, null); Toast("▶", $"Playing a 30 s preview of “{r.Title}”"); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Paths.Log(ex);
            Toast("!", "The preview could not be loaded. " + Remote.Classify(ex).Describe("previews"));
        }
    }

    /// <summary>Finds out in the background which online songs have a preview, so every row shows its real state. Failures are silent here.</summary>
    async Task ResolvePlayback(IEnumerable<PopularItem> items)
    {
        var cts = _artistCts;
        if (cts == null) return;
        foreach (var it in items.Where(i => !i.IsLocal && i.Remote != null && !i.Remote.PreviewChecked && !i.Remote.HasPreview).Take(12).ToList())
        {
            if (cts.IsCancellationRequested) return;
            try
            {
                await AudioProviderService.Instance.GetPreviewSourceAsync(it.Remote, cts.Token);
                it.Remote.PreviewChecked = true;                      // asked and answered (even if the answer is "none")
                it.Badge = BadgeFor(it.Remote);
            }
            catch (OperationCanceledException) { return; }
            catch { /* the audio provider is optional: leave the row as "ONLINE" */ }
        }
    }

    void PlayArtist(bool shuffle)
    {
        if (_popTracks.Count > 0)
        {
            C.SetShuffle(shuffle);
            C.PlayTracks(_popTracks.ToList(), shuffle ? _rng.Next(_popTracks.Count) : 0, null);
            return;
        }
        var remote = _popAll.FirstOrDefault(i => !i.IsLocal && i.Remote?.HasPreview != false);
        if (remote != null) _ = PlayRemote(remote, false);
        else Toast("♪", "No playable songs for this artist yet");
    }

    void ToggleArtistFavorite()
    {
        var mine = ArtistTracks();
        if (mine.Count == 0) return;
        C.SetFavorite(mine, !mine.All(t => t.IsFavorite));
    }

    void UpdateArtistFav()
    {
        var mine = ArtistTracks();
        bool all = mine.Count > 0 && mine.All(t => t.IsFavorite);
        BtnArtFav.Content = all ? "♥" : "♡";
        BtnArtFav.IsEnabled = mine.Count > 0;
        BtnArtFav.ToolTip = mine.Count == 0 ? "Favorite (no songs in your library)" : all ? "Remove all their songs from favorites" : "Add all their songs to favorites";
    }

    void ShowArtistMore()
    {
        var mine = _popTracks;
        var m = new ContextMenu { PlacementTarget = BtnArtMore, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        StyleMenu(m);
        if (mine.Count > 0)
        {
            m.Items.Add(Item("＋", "Add all to Queue", () => C.Enqueue(mine.ToList())));
            m.Items.Add(Item("»", "Play all Next", () => C.EnqueueNext(mine.ToList())));
            var pl = NewItem("Add all to Playlist", "☰");
            foreach (var n in L.PlaylistNames.ToList())
            {
                string name = n;
                pl.Items.Add(Item(null, name, () => { L.AddToPlaylist(name, mine); Toast("☰", $"Added {mine.Count} songs to {name}"); }));
            }
            if (pl.Items.Count > 0) m.Items.Add(pl);
            m.Items.Add(Sep());
        }
        if (mine.Count > 0) m.Items.Add(Item("♪", "Open in Artists", () => Navigate("Artists", _artistName)));
        m.Items.Add(Item("⟳", "Refresh artist info", () => RetryArtist(true)));
        m.Items.Add(Item("▤", "Open artist pictures folder", OpenPicturesFolder));
        m.IsOpen = true;
    }

    // ================================================================== YOU MIGHT ALSO LIKE (remote, merged with the library)
    void RenderRecommended(ArtistProfile p, bool online)
    {
        var (state, message) = St(p?.Recommended, online);
        var remote = state == RemoteState.Ok ? p.Recommended.Items.Take(12).Select(r => r.ToRemote()) : Enumerable.Empty<RemoteTrack>();
        _recAll = Merge(remote, null, false);                      // online recommendations first, then marked LOCAL where you own them
        RecList.ItemsSource = _recAll;
        _ = LoadRemoteThumbs(_recAll);
        _ = ResolvePlayback(_recAll);

        RecSub.Text = _artistSeed != null ? $"Similar to “{_artistSeed}”" : "Songs that listeners of this artist also play";
        SetSkeleton(RecSkeleton, 3, state == RemoteState.Loading && _recAll.Count == 0);

        string tip = Remote.HasLastFm ? "" : "  Tip: a free Last.fm API key (Settings → Library) gives the best recommendations.";
        RecState.Text = state switch
        {
            RemoteState.Failed or RemoteState.Offline => message + tip,
            RemoteState.NoData => "No similar tracks found." + tip,
            RemoteState.Disabled => "Recommendations need online discovery (Settings → Library).",
            RemoteState.Ok => p.Recommended.Message,              // e.g. "Showing saved recommendations." / "These songs are from your library."
            _ => ""
        };
    }

    // ================================================================== LATEST RELEASES
    void RenderReleases(ArtistProfile p, bool online)
    {
        var (state, _) = St(p?.Releases, online);
        bool show = state == RemoteState.Ok;
        ReleaseTitle.Visibility = ReleaseItems.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ReleaseState.Text = "";
        if (!show) { ReleaseItems.ItemsSource = null; return; }

        var existing = (ReleaseItems.ItemsSource as List<RelatedCard>)?.Select(c => c.Name).ToList();
        if (existing != null && existing.SequenceEqual(p.Releases.Items.Select(r => r.Title))) return;
        var cards = p.Releases.Items.Select(r => new RelatedCard
        {
            Name = r.Title, ImageUrl = r.CoverUrl, Link = r.Url,
            Sub = string.Join(" · ", new[] { r.Date.Length >= 4 ? r.Date.Substring(0, 4) : "", r.Type }.Where(x => !string.IsNullOrEmpty(x)))
        }).ToList();
        ReleaseItems.ItemsSource = cards;
        _ = LoadCardImages(cards, 220);
    }

    void OnReleaseClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is Button b && b.Tag is string url) OpenUrl(url);
    }

    // ================================================================== ABOUT
    void RenderBio(ArtistProfile p)
    {
        bool online = Remote.Enabled;
        var info = p?.Info;
        string bio = info?.Bio;
        var state = !online ? RemoteState.Disabled : p == null ? RemoteState.Loading : p.InfoState == RemoteState.NotLoaded ? RemoteState.Loading : p.InfoState;

        if (string.IsNullOrWhiteSpace(bio))
        {
            BioText.Text = "";
            BioFade.Visibility = BtnReadMore.Visibility = Visibility.Collapsed;
            BioSource.Text = "";
            BioMissing.Text = state switch
            {
                RemoteState.Loading => "Loading biography…",
                RemoteState.Disabled => "Online artist info is switched off in Settings.",
                RemoteState.Failed or RemoteState.Offline => p.InfoMessage,
                _ => Remote.HasLastFm ? "Biography unavailable"
                   : "Biography unavailable\nA free Last.fm API key (Settings → Library) adds biographies for more artists."
            };
        }
        else
        {
            BioMissing.Text = "";
            BioText.Text = bio;
            bool longBio = bio.Length > 560;
            BioText.MaxHeight = longBio && !_bioExpanded ? 140 : double.PositiveInfinity;
            BioFade.Visibility = longBio && !_bioExpanded ? Visibility.Visible : Visibility.Collapsed;
            BtnReadMore.Visibility = longBio ? Visibility.Visible : Visibility.Collapsed;
            BtnReadMore.Content = _bioExpanded ? "Show less" : "Read more";
            BioSource.Text = string.IsNullOrEmpty(info.BioSource) ? "" : $"Source: {info.BioSource}";
        }

        var facts = new List<string>();
        if (info != null)
        {
            if (!string.IsNullOrEmpty(info.Type)) facts.Add($"Type:  {info.Type}");
            if (!string.IsNullOrEmpty(info.Country)) facts.Add($"Country:  {CountryName(info.Country)}");
            if (info.BeginYear > 0) facts.Add($"{(info.Ended ? "Active from" : "Active since")}:  {info.BeginYear}");
            if (info.Listeners > 0) facts.Add($"Listeners:  {FormatCount(info.Listeners)}");
        }
        FactsText.Text = facts.Count > 0 ? string.Join("\n", facts) : state == RemoteState.Loading ? "Loading…" : "No details available.";

        TagsPanel.Children.Clear();
        foreach (var tag in info?.Tags ?? new List<string>())
        {
            var chip = new Border { Padding = new Thickness(11, 5, 11, 5), Margin = new Thickness(0, 0, 8, 8), BorderThickness = new Thickness(1) };
            chip.SetResourceReference(Border.BorderBrushProperty, "Accent");
            chip.SetResourceReference(Border.CornerRadiusProperty, "RadiusLg");
            var t = new TextBlock { Text = tag, FontSize = 12 };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            chip.Child = t;
            TagsPanel.Children.Add(chip);
        }
        TagsMissing.Text = TagsPanel.Children.Count > 0 ? "" : state == RemoteState.Loading ? "Loading…" : "No genre information available.";
    }

    // ================================================================== RELATED ARTISTS
    void RenderRelated(ArtistProfile p, bool online)
    {
        var (state, message) = St(p?.Similar, online);
        var list = state == RemoteState.Ok ? p.Similar.Items : new List<RelatedArtist>();

        SetSkeleton(RelSkeleton, 4, state == RemoteState.Loading);
        RelatedEmpty.Text = state switch
        {
            RemoteState.Failed or RemoteState.Offline => message,
            RemoteState.NoData => "No related artists were found, even after trying every source.",
            RemoteState.Disabled => "Related artists need online discovery (Settings → Library).",
            _ => ""
        };

        var existing = (RelatedItems.ItemsSource as List<RelatedCard>)?.Select(c => c.Name).ToList();
        if (existing != null && existing.SequenceEqual(list.Select(r => r.Name))) return;        // unchanged: keep loaded pictures
        var cards = list.Select(r =>
        {
            int n = L.Tracks.Count(t => Cmp.SameArtist(t.Artist, r.Name));
            string sub = n > 0 ? $"{n} song{(n == 1 ? "" : "s")} in your library" : r.Match > 0 ? $"{Math.Round(r.Match * 100)}% similar" : "";
            return new RelatedCard { Name = r.Name, Sub = sub, ImageUrl = r.ImageUrl };
        }).ToList();
        RelatedItems.ItemsSource = cards.Count > 0 ? cards : null;
        if (cards.Count > 0) _ = LoadCardImages(cards, 260, true);
    }

    async Task LoadCardImages(List<RelatedCard> cards, int decodeWidth, bool resolveArtistImage = false)
    {
        var cts = _artistCts;
        if (cts == null) return;
        var svc = ArtistImageService.Instance;
        foreach (var c in cards)
        {
            if (cts.IsCancellationRequested) return;
            try
            {
                string url = c.ImageUrl;
                if (string.IsNullOrEmpty(url) && resolveArtistImage) url = await svc.GetBestArtistImageAsync(c.Name, cts.Token);
                if (string.IsNullOrEmpty(url)) continue;
                var img = await svc.LoadAsync(url, decodeWidth, cts.Token);
                if (img != null && !cts.IsCancellationRequested) c.Image = img;
            }
            catch (OperationCanceledException) { return; }
            catch { /* this card keeps its initial letter */ }
        }
    }

    void OnRelatedClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is Button b && b.Tag is string name) OpenArtist(name);
    }

    // ================================================================== loading placeholders
    void SetSkeleton(Panel host, int rows, bool show)
    {
        host.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) { host.Children.Clear(); return; }
        if (host.Children.Count == rows) return;
        host.Children.Clear();
        for (int i = 0; i < rows; i++)
        {
            var bar = new Border { Style = (Style)FindResource("SkeletonBar"), Opacity = 0.6 };
            if (S.Animations != AnimMode.Off)
                bar.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 0.85, TimeSpan.FromMilliseconds(850))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromMilliseconds(i * 120) });
            host.Children.Add(bar);
        }
    }

    // ================================================================== online song menu (same reusable menu mechanism)
    bool BuildRemoteMenu(ContextMenu menu, PopularItem pi)
    {
        if (pi?.Remote == null) return false;
        StyleMenu(menu);
        menu.Items.Clear();
        var r = pi.Remote;
        bool canPlay = r.HasPreview || !r.PreviewChecked;           // unknown = not asked yet; a known "none" hides the Play entries
        if (canPlay)
        {
            menu.Items.Add(Item("▶", "Play Online (30 s preview)", () => _ = PlayRemote(pi, false)));
            menu.Items.Add(Item("＋", "Add to Queue", () => _ = PlayRemote(pi, true)));
        }
        else
        {
            var none = NewItem("Online playback is not available", "♪");
            none.IsEnabled = false;
            menu.Items.Add(none);
        }
        string link = !string.IsNullOrEmpty(r.Url) ? r.Url : "";
        if (link.Length > 0) menu.Items.Add(Item("↗", "Open Online", () => OpenUrl(link)));
        menu.Items.Add(Sep());
        menu.Items.Add(Item("♪", "Show Artist", () => OpenArtist(pi.Artist)));
        menu.Items.Add(Item("≈", "Similar Tracks", () => OpenArtist(pi.Artist, pi.Title)));
        if (!string.IsNullOrEmpty(r.AlbumUrl)) menu.Items.Add(Item("◉", "Show Album Online", () => OpenUrl(r.AlbumUrl)));
        return true;
    }
}
