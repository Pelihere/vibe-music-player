using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace PortablePlayer;

public partial class MainWindow : Window
{
    static App A => App.Instance;
    static PlaybackCore C => App.Instance.Core;
    static LibraryStore L => App.Instance.Library;
    static AppSettings S => App.Instance.Settings;

    public bool IsDisposed { get; private set; }

    // navigation state
    string _page = "Library";            // what is on screen (can be "Search")
    string _navPage = "Library";         // the sidebar page (what Esc / clearing the search returns to)
    string _view = "Library";            // which library-style view is active
    string _pendingGroup;
    List<Track> _shown = new();
    bool _building, _clearing, _scanning, _miniShown, _dirty;
    int _lastSec = -1;
    readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    readonly Random _rng = new();
    UIElement[] _pages;

    // drag & drop
    Point _dragStart;
    QueueEntry _dragEntry;
    bool _dragFromSongs;
    ListBoxItem _dropTarget;

    public MainWindow()
    {
        InitializeComponent();

        _pages = new UIElement[] { PageGramophone, PageLibrary, PageSearch, PageQueue, PageLyrics, PageStats, PageArtist, PageSettings };
        foreach (var p in _pages) p.RenderTransform = new TranslateTransform();

        // themed bars / progress
        MSeek.BindTheme("Accent", "Border", "Fg");
        MVol.BindTheme("Fg", "Border", "Fg");

        // title bar
        BtnMin.Click += (_, _) => WindowState = WindowState.Minimized;          // a real minimize: the app and the music keep running
        BtnMax.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        BtnClose.Click += (_, _) => Close();                                    // close = hide to tray (see OnClosing)
        BtnTheme.Click += (_, _) => ShowThemeMenu();
        Search.TextChanged += (_, _) => OnSearchTyped();
        Search.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { ClearSearch(true); e.Handled = true; } };
        BtnClearSearch.Click += (_, _) => { ClearSearch(true); Search.Focus(); };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); RunSearchNow(); };

        // sidebar
        Nav.SelectionChanged += (_, _) => OnNav();
        BtnAddFolder.Click += async (_, _) => await AddFolder();
        BtnEmptyAdd.Click += async (_, _) => await AddFolder();
        BtnRescan.Click += async (_, _) => await Rescan();
        NavQueue.DragOver += (_, e) => { bool ok = e.Data.GetDataPresent("VibeTracks"); e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None; Dnd.SetIsTarget(NavQueue, ok); e.Handled = true; };
        NavQueue.DragLeave += (_, _) => Dnd.SetIsTarget(NavQueue, false);
        NavQueue.Drop += (_, e) =>
        {
            Dnd.SetIsTarget(NavQueue, false);
            if (e.Data.GetData("VibeTracks") is List<Track> l) C.Enqueue(l);
            e.Handled = true;
        };

        // library page
        BtnPlayAll.Click += (_, _) => PlayShown(false);
        BtnShuffleAll.Click += (_, _) => PlayShown(true);
        BtnNewPl.Click += (_, _) => NewPlaylist();
        BtnDelPl.Click += (_, _) => DeletePlaylist();
        Groups.SelectionChanged += (_, _) => { if (!_building) RefreshLibrary(); };
        SongList.MouseDoubleClick += (_, e) => PlayFromRow(SongList, _shown, SelectedPlaylist, e);
        SongList.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && SongList.SelectedItem is Track t) { C.PlayTracks(_shown, _shown.IndexOf(t), SelectedPlaylist); e.Handled = true; } };
        SongList.ContextMenuOpening += (_, e) => { if (!FillTrackMenu(RowMenu, SongList, _shown, SelectedPlaylist)) e.Handled = true; };
        SongList.AddHandler(Button.ClickEvent, new RoutedEventHandler(OnMoreClick));
        SrSongs.AddHandler(Button.ClickEvent, new RoutedEventHandler(OnMoreClick));
        BtnSort.Click += (_, _) => ShowLibrarySort();
        SongList.PreviewMouseLeftButtonDown += OnSongsDown;
        SongList.PreviewMouseMove += OnSongsMove;
        SongList.PreviewMouseRightButtonDown += (_, e) => SelectUnderMouse(SongList, e);

        // search results
        SrSongs.ContextMenu = new ContextMenu();
        SrSongs.MouseDoubleClick += (_, e) => PlayFromRow(SrSongs, SrSongs.ItemsSource as List<Track>, null, e);
        SrSongs.ContextMenuOpening += (_, e) => { if (!FillTrackMenu(SrSongs.ContextMenu, SrSongs, SrSongs.ItemsSource as List<Track>, null)) e.Handled = true; };
        SrSongs.PreviewMouseRightButtonDown += (_, e) => SelectUnderMouse(SrSongs, e);
        foreach (var lb in new[] { SrSongs, SrArtists, SrAlbums, SrPlaylists })
            lb.PreviewMouseWheel += (_, e) => { PageSearch.ScrollToVerticalOffset(PageSearch.VerticalOffset - e.Delta); e.Handled = true; };
        SrArtists.SelectionChanged += (_, _) => OpenGroup(SrArtists, "Artists");
        SrAlbums.SelectionChanged += (_, _) => OpenGroup(SrAlbums, "Albums");
        SrPlaylists.SelectionChanged += (_, _) => OpenGroup(SrPlaylists, "Playlists");

        // queue
        BtnClearQueue.Click += (_, _) => C.ClearUpcoming();
        QueueList.AddHandler(Button.ClickEvent, new RoutedEventHandler(OnQueueButton));
        QueueList.MouseDoubleClick += (_, e) =>
        {
            if (UiUtil.FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return;
            if (UiUtil.FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is QueueEntry qe) C.PlayIndex(qe.QueueIndex);
        };
        QueueList.PreviewMouseLeftButtonDown += OnQueueDown;
        QueueList.PreviewMouseMove += OnQueueMove;
        QueueList.DragOver += OnQueueDragOver;
        QueueList.DragLeave += (_, _) => SetDropTarget(null);
        QueueList.Drop += OnQueueDrop;
        QueueList.ContextMenuOpening += (_, e) => { if (!FillQueueMenu()) e.Handled = true; };
        QueueList.PreviewMouseRightButtonDown += (_, e) => SelectUnderMouse(QueueList, e);

        // gramophone page + mini player
        Deck.QueueClicked += () => Navigate("Queue");
        MiniArt.MouseLeftButtonUp += (_, _) => OpenCurrentArtist();
        QueueNowArt.Cursor = Cursors.Hand;
        QueueNowArt.ToolTip = "Open artist profile";
        QueueNowArt.MouseLeftButtonUp += (_, _) => OpenCurrentArtist();
        MExpand.Click += (_, _) => Navigate("Gramophone");
        MPlay.Click += (_, _) => C.Toggle();
        MPrev.Click += (_, _) => C.Previous();
        MNext.Click += (_, _) => C.Next();
        MShuffle.Click += (_, _) => C.SetShuffle(!C.Shuffle);
        MRepeat.Click += (_, _) => C.CycleRepeat();
        MFav.Click += (_, _) => { var t = C.Current; if (t != null) C.SetFavorite(new[] { t }, !t.IsFavorite); };
        MSeek.Dragging += f => MTimeCur.Text = Fmt.Time(f * C.Duration.TotalSeconds);
        MSeek.Commit += f => C.Seek(f);
        MVol.Dragging += v => C.SetVolume(v);
        MVol.Commit += v => C.SetVolume(v);

        // window-wide
        DragOver += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; } };
        Drop += OnFileDrop;

        IsVisibleChanged += (_, _) => FlushDirty();
        C.Changed += OnCore;
        C.Notice += OnNotice;
        L.Changed += OnLibraryChanged;
        Closed += (_, _) =>
        {
            IsDisposed = true;
            C.Changed -= OnCore;
            C.Notice -= OnNotice;
            L.Changed -= OnLibraryChanged;
        };

        InitArtist();
        InitSettingsPage();
        InitLyrics();
        InitMeta();

        UpdateMini();
        int start = Nav.Items.Cast<ListBoxItem>().ToList().FindIndex(i => (string)i.Tag == S.StartPage);
        Nav.SelectedIndex = start >= 0 ? start : 1;
    }

    // ================================================================== window behaviour
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!A.Exiting)
        {
            e.Cancel = true;             // close = hide to the tray; the music keeps playing
            A.HideMainToTray();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        bool max = WindowState == WindowState.Maximized;
        // a borderless maximized window would overhang the screen by the resize border
        Root.Margin = max ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        BtnMax.Content = max ? "\uE923" : "\uE922";
        BtnMax.ToolTip = max ? "Restore" : "Maximize";
        FlushDirty();
        // Minimized stays minimized: the window keeps existing, nothing else happens.
    }

    /// <summary>Opens the queue page (used by the floating panel and the deck).</summary>
    public void ShowQueue() => Navigate("Queue");

    // ================================================================== navigation
    public void Navigate(string page, string group = null)
    {
        _pendingGroup = group;
        foreach (ListBoxItem it in Nav.Items)
        {
            if ((string)it.Tag != page) continue;
            if (it.IsSelected) { ClearSearch(false); ShowPage(page); }
            else it.IsSelected = true;
            return;
        }
    }

    void OnNav()
    {
        if (Nav.SelectedItem is not ListBoxItem it) return;
        ClearSearch(false);
        ShowPage((string)it.Tag);
    }

    void ShowPage(string page)
    {
        _page = _navPage = page;
        UIElement target = page switch
        {
            "Gramophone" => PageGramophone,
            "Queue" => PageQueue,
            "Lyrics" => PageLyrics,
            "Statistics" => PageStats,
            "Settings" => PageSettings,
            "Artist" => PageArtist,
            _ => PageLibrary
        };

        switch (page)
        {
            case "Queue": RebuildQueue(); break;
            case "Lyrics": OpenLyrics(); break;
            case "Statistics": RenderStats(); break;
            case "Settings": OpenSettingsPage(); break;
            case "Gramophone": case "Artist": break;
            default:
                _view = page;
                _building = true;
                RebuildGroups(_pendingGroup);
                _building = false;
                RefreshLibrary();
                break;
        }
        _pendingGroup = null;
        Present(target, true);
        SetMini(page != "Gramophone");
    }

    void Present(UIElement target, bool animate)
    {
        foreach (var p in _pages) if (!ReferenceEquals(p, target)) p.Visibility = Visibility.Collapsed;
        target.Visibility = Visibility.Visible;
        if (animate) Reveal(target);
    }

    /// <summary>Short fade + slide for page changes (Full), plain fade (Reduced), nothing (Off).</summary>
    void Reveal(UIElement el)
    {
        var mode = S.Animations;
        var tt = (TranslateTransform)el.RenderTransform;
        el.BeginAnimation(OpacityProperty, null);
        tt.BeginAnimation(TranslateTransform.YProperty, null);
        if (mode == AnimMode.Off) { el.Opacity = 1; tt.Y = 0; return; }
        double ms = mode == AnimMode.Full ? 240 : 120;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        el.Opacity = 0;
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
        if (mode == AnimMode.Full)
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
    }

    // ================================================================== library-style pages
    bool IsGroupView => _view is "Artists" or "Albums" or "Genres" or "Playlists";
    string SelectedPlaylist => _view == "Playlists" ? (Groups.SelectedItem as GroupItem)?.Name : null;

    static string GenreOf(Track t) => string.IsNullOrWhiteSpace(t.Genre) ? "Unknown genre" : t.Genre;

    /// <summary>Groups by name ignoring case and spacing; the first spelling found is the one that is displayed.</summary>
    static List<GroupItem> MakeGroups(IEnumerable<Track> src, Func<Track, string> name) =>
        src.GroupBy(t => Cmp.Key(name(t)))
           .Select(g => new GroupItem { Name = Cmp.Norm(name(g.First())), Count = g.Count(), Sample = g.First() })
           .OrderBy(g => g.Name, Cmp.Ic).ToList();

    void RebuildGroups(string select = null)
    {
        string keep = select ?? (Groups.SelectedItem as GroupItem)?.Name;
        var items = new List<GroupItem>();

        switch (_view)
        {
            case "Artists": items = MakeGroups(L.Tracks, t => t.Artist); break;
            case "Albums": items = MakeGroups(L.Tracks, t => t.Album); break;
            case "Genres": items = MakeGroups(L.Tracks, GenreOf); break;
            case "Playlists":
                items = L.PlaylistNames.Select(n =>
                {
                    var tr = L.PlaylistTracks(n);
                    return new GroupItem { Name = n, Count = tr.Count, Sample = tr.FirstOrDefault() };
                }).ToList();
                break;
        }
        Groups.ItemsSource = items;
        var sel = items.FirstOrDefault(g => Cmp.Eq(g.Name, keep)) ?? items.FirstOrDefault();
        Groups.SelectedItem = sel;
        if (sel != null) Groups.ScrollIntoView(sel);
    }

    void RefreshLibrary()
    {
        GroupPane.Visibility = IsGroupView ? Visibility.Visible : Visibility.Collapsed;
        PlBar.Visibility = _view == "Playlists" ? Visibility.Visible : Visibility.Collapsed;

        string g = (Groups.SelectedItem as GroupItem)?.Name;
        string title = _view;
        IEnumerable<Track> src;
        switch (_view)
        {
            case "Favorites": src = LibraryStore.Sort(L.Tracks.Where(t => t.IsFavorite)); break;
            case "Artists": src = g == null ? Enumerable.Empty<Track>() : LibraryStore.Sort(L.Tracks.Where(t => Cmp.Eq(t.Artist, g))); title = g ?? "Artists"; break;
            case "Albums": src = g == null ? Enumerable.Empty<Track>() : LibraryStore.Sort(L.Tracks.Where(t => Cmp.Eq(t.Album, g))); title = g ?? "Albums"; break;
            case "Genres": src = g == null ? Enumerable.Empty<Track>() : LibraryStore.Sort(L.Tracks.Where(t => Cmp.Eq(GenreOf(t), g))); title = g ?? "Genres"; break;
            case "Playlists": src = g == null ? Enumerable.Empty<Track>() : L.PlaylistTracks(g); title = g ?? "Playlists"; break;
            default: src = LibraryStore.Sort(L.Tracks); title = "Your Library"; break;
        }
        _shown = Sorting.Apply(src, S.LibrarySort, S.LibrarySortDesc).ToList();
        SongList.ItemsSource = _shown;
        BtnSort.ToolTip = S.LibrarySort == "Default" ? "Sort: default order" : $"Sort: {S.LibrarySort} ({(S.LibrarySortDesc ? "descending" : "ascending")})";

        ViewTitle.Text = title;
        ViewSub.Text = _shown.Count == 0 ? "" : $"{_shown.Count} song{(_shown.Count == 1 ? "" : "s")}  ·  {Duration(_shown.Sum(t => t.DurationSec))}";

        bool empty = _shown.Count == 0;
        SongList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        BtnPlayAll.IsEnabled = BtnShuffleAll.IsEnabled = !empty;
        BtnEmptyAdd.Visibility = L.Tracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = L.Tracks.Count == 0 ? "Your library is empty.\nAdd a music folder, or drop files and folders onto this window."
            : _view == "Favorites" ? "No favorites yet.\nTap the heart on a song to keep it here."
            : _view == "Playlists" ? (g == null ? "No playlists yet. Create one with “New playlist”." : "This playlist is empty.\nRight-click a song and choose “Add to Playlist”.")
            : "Nothing here yet.";
    }

    static string Duration(double secs)
    {
        var t = TimeSpan.FromSeconds(secs);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{Math.Max(1, (int)Math.Round(t.TotalMinutes))} min";
    }

    void PlayShown(bool shuffle)
    {
        if (_shown.Count == 0) return;
        C.SetShuffle(shuffle);
        C.PlayTracks(_shown.ToList(), shuffle ? _rng.Next(_shown.Count) : 0, SelectedPlaylist);
    }

    void PlayFromRow(ListBox lb, List<Track> source, string playlist, MouseButtonEventArgs e)
    {
        if (source == null) return;
        if (UiUtil.FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not Track t) return;
        int idx = source.IndexOf(t);
        if (idx >= 0) C.PlayTracks(source.ToList(), idx, playlist);
    }

    static void SelectUnderMouse(ListBox lb, MouseButtonEventArgs e)
    {
        if (UiUtil.FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { IsSelected: false } item)
        {
            if (lb.SelectionMode != SelectionMode.Single) lb.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    // ================================================================== search
    void OnSearchTyped()
    {
        bool has = Search.Text.Length > 0;
        SearchHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        BtnClearSearch.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (_clearing) return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    /// <summary>Empties the search box; with leave = true it also returns from the results page.</summary>
    void ClearSearch(bool leave)
    {
        _searchTimer.Stop();
        if (Search.Text.Length > 0)
        {
            _clearing = true;
            Search.Clear();
            _clearing = false;
        }
        if (leave && _page == "Search") ShowPage(_navPage);
    }

    void RunSearchNow()
    {
        string q = Search.Text.Trim();
        if (q.Length == 0) { if (_page == "Search") ShowPage(_navPage); return; }

        var songs = L.Tracks.Where(t => Has(t.Title, q) || Has(t.Artist, q) || Has(t.Album, q) || Has(t.Genre, q))
            .OrderBy(t => t.Title != null && t.Title.StartsWith(q, StringComparison.CurrentCultureIgnoreCase) ? 0 : Has(t.Title, q) ? 1 : Has(t.Artist, q) ? 2 : 3)
            .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        var artists = MakeGroups(L.Tracks.Where(t => Has(t.Artist, q)), t => t.Artist).Take(8).ToList();
        var albums = MakeGroups(L.Tracks.Where(t => Has(t.Album, q)), t => t.Album).Take(8).ToList();
        var lists = L.PlaylistNames.Where(n => Has(n, q)).Select(n =>
        {
            var tr = L.PlaylistTracks(n);
            return new GroupItem { Name = n, Count = tr.Count, Sample = tr.FirstOrDefault() };
        }).Take(8).ToList();

        int total = songs.Count + artists.Count + albums.Count + lists.Count;
        SrSongs.ItemsSource = songs.Take(30).ToList();
        SrArtists.ItemsSource = artists;
        SrAlbums.ItemsSource = albums;
        SrPlaylists.ItemsSource = lists;
        SecSongs.Visibility = songs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SecArtists.Visibility = artists.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SecAlbums.Visibility = albums.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SecPlaylists.Visibility = lists.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        SearchTitle.Text = $"“{q}”";
        var parts = new List<string>();
        if (songs.Count > 0) parts.Add($"{songs.Count} song{(songs.Count == 1 ? "" : "s")}");
        if (artists.Count > 0) parts.Add($"{artists.Count} artist{(artists.Count == 1 ? "" : "s")}");
        if (albums.Count > 0) parts.Add($"{albums.Count} album{(albums.Count == 1 ? "" : "s")}");
        if (lists.Count > 0) parts.Add($"{lists.Count} playlist{(lists.Count == 1 ? "" : "s")}");
        SearchSub.Text = total == 0 ? "No matches" : string.Join("  ·  ", parts);
        SearchEmpty.Text = total == 0 ? "Nothing found. Check the spelling or try a shorter word." : "";

        bool wasSearchVisible = _page == "Search";
        _page = "Search";
        PageSearch.ScrollToTop();
        Present(PageSearch, !wasSearchVisible);
        SetMini(true);
    }

    static bool Has(string s, string q) => Cmp.Has(s, q);

    void OpenGroup(ListBox lb, string page)
    {
        if (lb.SelectedItem is not GroupItem g) return;
        string name = g.Name;
        lb.SelectedItem = null;
        Navigate(page, name);
    }

    // ================================================================== queue / up next
    void RebuildQueue()
    {
        var list = new List<QueueEntry>();
        for (int i = C.Index + 1; i < C.Queue.Count; i++)
            list.Add(new QueueEntry { Track = C.Queue[i], QueueIndex = i, Number = i - C.Index });
        QueueList.ItemsSource = list;

        double secs = list.Sum(q => q.Track.DurationSec);
        QueueSub.Text = list.Count == 0 ? "" : $"{list.Count} song{(list.Count == 1 ? "" : "s")}  ·  {Duration(secs)}";
        BtnClearQueue.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var cur = C.Current;
        QueueNow.Visibility = cur != null ? Visibility.Visible : Visibility.Collapsed;
        QueueNowTitle.Text = cur?.Title ?? "";
        QueueNowArtist.Text = cur?.Artist ?? "";
        if (C.Artwork != null) QueueNowArt.Background = new ImageBrush(C.Artwork) { Stretch = Stretch.UniformToFill };
        else QueueNowArt.SetResourceReference(Border.BackgroundProperty, "Surface");

        QueueList.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueEmpty.Visibility = list.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        QueueEmpty.Text = cur == null ? "Nothing is queued.\nPlay a song from the library to start."
            : "Nothing up next.\nRight-click songs and choose “Add to Queue”, or drag them onto Up Next in the sidebar.";
    }

    void OnQueueButton(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is Button b && b.DataContext is QueueEntry qe) C.RemoveFromQueue(qe.QueueIndex);
    }

    void OnQueueDown(object sender, MouseButtonEventArgs e)
    {
        _dragEntry = null;
        if (UiUtil.FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return;
        _dragEntry = UiUtil.FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as QueueEntry;
        _dragStart = e.GetPosition(null);
    }

    void OnQueueMove(object sender, MouseEventArgs e)
    {
        if (_dragEntry == null || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(null) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var entry = _dragEntry;
        _dragEntry = null;
        try { DragDrop.DoDragDrop(QueueList, new DataObject("VibeQueueEntry", entry), DragDropEffects.Move); }
        finally { SetDropTarget(null); }
    }

    void OnQueueDragOver(object sender, DragEventArgs e)
    {
        bool entry = e.Data.GetDataPresent("VibeQueueEntry"), tracks = e.Data.GetDataPresent("VibeTracks");
        if (!entry && !tracks) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        e.Effects = entry ? DragDropEffects.Move : DragDropEffects.Copy;
        SetDropTarget(UiUtil.FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject));

        // auto-scroll near the edges
        var sv = FindScroll(QueueList);
        if (sv != null)
        {
            double y = e.GetPosition(QueueList).Y;
            if (y < 28) sv.ScrollToVerticalOffset(sv.VerticalOffset - 18);
            else if (y > QueueList.ActualHeight - 28) sv.ScrollToVerticalOffset(sv.VerticalOffset + 18);
        }
        e.Handled = true;
    }

    void OnQueueDrop(object sender, DragEventArgs e)
    {
        var target = _dropTarget?.DataContext as QueueEntry;
        SetDropTarget(null);
        if (e.Data.GetData("VibeQueueEntry") is QueueEntry from)
        {
            int to = target?.QueueIndex ?? C.Queue.Count - 1;
            C.MoveInQueue(from.QueueIndex, to);
        }
        else if (e.Data.GetData("VibeTracks") is List<Track> tracks)
        {
            if (target != null) C.InsertIntoQueue(tracks, target.QueueIndex); else C.Enqueue(tracks);
        }
        e.Handled = true;
    }

    void SetDropTarget(ListBoxItem item)
    {
        if (ReferenceEquals(item, _dropTarget)) return;
        if (_dropTarget != null) Dnd.SetIsTarget(_dropTarget, false);
        _dropTarget = item;
        if (_dropTarget != null) Dnd.SetIsTarget(_dropTarget, true);
    }

    static ScrollViewer FindScroll(DependencyObject d)
    {
        if (d == null) return null;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is ScrollViewer sv) return sv;
            var r = FindScroll(c);
            if (r != null) return r;
        }
        return null;
    }

    // dragging songs out of the library (drop on the queue page or on "Up Next" in the sidebar)
    void OnSongsDown(object sender, MouseButtonEventArgs e)
    {
        _dragFromSongs = UiUtil.FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) != null
                         && UiUtil.FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(e.OriginalSource as DependencyObject) == null
                         && UiUtil.FindAncestor<Button>(e.OriginalSource as DependencyObject) == null;
        _dragStart = e.GetPosition(null);
    }

    void OnSongsMove(object sender, MouseEventArgs e)
    {
        if (!_dragFromSongs || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(null) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragFromSongs = false;
        var sel = SongList.SelectedItems.Cast<Track>().ToList();
        if (sel.Count == 0) return;
        DragDrop.DoDragDrop(SongList, new DataObject("VibeTracks", sel), DragDropEffects.Copy);
    }

    // ================================================================== context menus
    static TextBlock Glyph(string g) => new() { Text = g, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };

    MenuItem Item(string glyph, string header, Action act)
    {
        var mi = NewItem(header, glyph);
        mi.Click += (_, _) => act();
        return mi;
    }

    MenuItem NewItem(string header, string glyph) =>
        new() { Header = header, Icon = glyph == null ? null : Glyph(glyph), Style = (Style)FindResource(typeof(MenuItem)) };

    Separator Sep() => new() { Style = (Style)FindResource(MenuItem.SeparatorStyleKey) };

    void StyleMenu(ContextMenu m)
    {
        m.Style = (Style)FindResource(typeof(ContextMenu));
        if (m.Tag as string == "styled") return;
        m.Tag = "styled";
        m.Opened += (_, _) =>
        {
            if (S.Animations == AnimMode.Off) return;
            m.Opacity = 0;
            m.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(S.Animations == AnimMode.Full ? 150 : 80)));
        };
    }

    /// <summary>Builds the song context menu for the current selection. Returns false when nothing is selected.</summary>
    bool FillTrackMenu(ContextMenu menu, ListBox lb, List<Track> source, string playlist)
    {
        if (lb.SelectedItems.Cast<object>().FirstOrDefault() is PopularItem remote && !remote.IsLocal)
            return BuildRemoteMenu(menu, remote);
        var sel = lb.SelectedItems.Cast<object>().Select(AsTrack).Where(t => t != null).ToList();
        return sel.Count > 0 && BuildTrackMenu(menu, sel, source, playlist);
    }

    static Track AsTrack(object o) => o as Track ?? (o as PopularItem)?.Track;

    /// <summary>The ⋮ button of a row: selects the row (if needed) and opens the very same menu the right-click shows.</summary>
    void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not Button b || !Equals(b.Tag, "more") || sender is not ListBox lb) return;
        if (b.DataContext is PopularItem rp && !rp.IsLocal)             // an ONLINE song: no file, different actions
        {
            e.Handled = true;
            lb.SelectedItem = rp;
            var rm = new ContextMenu { PlacementTarget = b, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            if (BuildRemoteMenu(rm, rp)) rm.IsOpen = true;
            return;
        }
        var t = AsTrack(b.DataContext);
        if (t == null) return;
        e.Handled = true;

        bool already = lb.SelectedItems.Cast<object>().Any(o => ReferenceEquals(AsTrack(o), t));
        if (!already)
        {
            if (lb.SelectionMode == SelectionMode.Single) lb.SelectedItem = b.DataContext;
            else { lb.SelectedItems.Clear(); lb.SelectedItems.Add(b.DataContext); }
        }

        List<Track> source = ReferenceEquals(lb, SongList) ? _shown : ReferenceEquals(lb, PopList) ? _popTracks : ReferenceEquals(lb, RecList) ? null : SrSongs.ItemsSource as List<Track>;
        string playlist = ReferenceEquals(lb, SongList) ? SelectedPlaylist : null;
        var sel = lb.SelectedItems.Cast<object>().Select(AsTrack).Where(x => x != null).ToList();

        var menu = new ContextMenu { PlacementTarget = b, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        if (BuildTrackMenu(menu, sel, source, playlist)) menu.IsOpen = true;
    }

    /// <summary>The one track-action menu. Right-click and the ⋮ button both build it here.</summary>
    bool BuildTrackMenu(ContextMenu menu, List<Track> sel, List<Track> source, string playlist)
    {
        StyleMenu(menu);
        menu.Items.Clear();
        var first = sel[0];

        menu.Items.Add(Item("▶", "Play", () =>
        {
            if (sel.Count == 1 && source != null && source.Contains(first)) C.PlayTracks(source.ToList(), source.IndexOf(first), playlist);
            else C.PlayTracks(sel, 0);
        }));
        menu.Items.Add(Item("＋", "Add to Queue", () => C.Enqueue(sel)));
        menu.Items.Add(Item("»", "Play Next", () => C.EnqueueNext(sel)));

        bool allFav = sel.All(t => t.IsFavorite);
        menu.Items.Add(Item(allFav ? "♥" : "♡", allFav ? "Remove from Favorites" : "Add to Favorites", () => C.SetFavorite(sel, !allFav)));

        var pl = NewItem("Add to Playlist", "☰");
        foreach (var n in L.PlaylistNames.ToList())
        {
            string name = n;
            pl.Items.Add(Item(null, name, () => { L.AddToPlaylist(name, sel); Toast("☰", sel.Count == 1 ? $"Added to {name}" : $"Added {sel.Count} songs to {name}"); }));
        }
        if (pl.Items.Count > 0) pl.Items.Add(Sep());
        pl.Items.Add(Item("＋", "New playlist…", () =>
        {
            string name = Prompt.Ask(this, "New playlist", "Playlist name")?.Trim();
            if (string.IsNullOrEmpty(name)) return;
            L.CreatePlaylist(name);
            L.AddToPlaylist(name, sel);
            Toast("☰", $"Added to {name}");
        }));
        menu.Items.Add(pl);

        if (playlist != null)
            menu.Items.Add(Item("✕", "Remove from Playlist", () => { L.RemoveFromPlaylist(playlist, sel); Toast("✕", "Removed from playlist"); }));

        menu.Items.Add(Sep());
        menu.Items.Add(Item("♪", "Show Artist", () => OpenArtist(first.Artist)));
        menu.Items.Add(Item("◉", "Show Album", () => Navigate("Albums", first.Album)));
        if (sel.Count == 1) menu.Items.Add(Item("≈", "Similar Tracks", () => OpenArtist(first.Artist, first.Title)));
        if (sel.Count == 1) menu.Items.Add(Item("✎", "Edit Metadata", () => OpenMetaEditor(first)));
        if (sel.Count == 1 && File.Exists(first.FilePath))
            menu.Items.Add(Item("▤", "Show in Explorer", () =>
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{first.FilePath}\"") { UseShellExecute = true }); }
                catch (Exception ex) { Paths.Log(ex); }
            }));
        menu.Items.Add(Sep());
        menu.Items.Add(Item("✕", "Remove from Library", () =>
        {
            var files = sel.Count == 1 ? sel[0].Title : $"{sel.Count} songs";
            bool yes = Dialogs.Confirm(this, "Remove from library?",
                "They disappear from Vibe's lists, playlists and statistics. Nothing is deleted from your disk.",
                "Remove", DialogKind.Warning,
                new[] { ("Selected", files), ("Files on disk", "stay untouched"), ("Add them back", "any time, by scanning the folder again") });
            if (yes) { L.Remove(sel); Toast("✕", sel.Count == 1 ? "Removed from library" : $"Removed {sel.Count} songs from library"); }
        }));
        return true;
    }

    bool FillQueueMenu()
    {
        if (QueueList.SelectedItem is not QueueEntry qe) return false;
        StyleMenu(QueueMenu);
        QueueMenu.Items.Clear();
        QueueMenu.Items.Add(Item("▶", "Play", () => C.PlayIndex(qe.QueueIndex)));
        QueueMenu.Items.Add(Item(qe.Track.IsFavorite ? "♥" : "♡", qe.Track.IsFavorite ? "Remove from Favorites" : "Add to Favorites",
            () => C.SetFavorite(new[] { qe.Track }, !qe.Track.IsFavorite)));
        QueueMenu.Items.Add(Sep());
        QueueMenu.Items.Add(Item("✕", "Remove from Queue", () => C.RemoveFromQueue(qe.QueueIndex)));
        return true;
    }

    void ShowThemeMenu()
    {
        var m = new ContextMenu { PlacementTarget = BtnTheme, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        StyleMenu(m);
        foreach (var t in ThemeManager.All)
        {
            string name = t.Name;
            var mi = Item(ThemeManager.Current == name ? "✓" : t.Glyph, name, () => { A.SetTheme(name); Toast("◐", $"Theme: {name}"); });
            m.Items.Add(mi);
        }
        m.IsOpen = true;
    }

    // ================================================================== playlists / scanning / drop
    void NewPlaylist()
    {
        string name = Prompt.Ask(this, "New playlist", "Playlist name")?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        L.CreatePlaylist(name);
        _building = true;
        RebuildGroups(name);
        _building = false;
        RefreshLibrary();
        Toast("☰", $"Created “{name}”");
    }

    void DeletePlaylist()
    {
        string name = (Groups.SelectedItem as GroupItem)?.Name;
        if (name == null) return;
        int n = (Groups.SelectedItem as GroupItem)?.Count ?? 0;
        bool yes = Dialogs.Confirm(this, "Delete this playlist?",
            "Only the playlist itself is removed. The songs in it stay in your library.",
            "Delete playlist", DialogKind.Danger,
            new[] { ("Playlist", name), ("Songs inside", n > 0 ? n.ToString() : "") });
        if (yes) { L.DeletePlaylist(name); Toast("✕", $"Playlist “{name}” deleted"); }
    }

    async Task AddFolder()
    {
        if (_scanning) return;
        using var dlg = new WinForms.FolderBrowserDialog { Description = "Choose a folder with music", UseDescriptionForTitle = true };
        if (dlg.ShowDialog() != WinForms.DialogResult.OK) return;
        await Scan(() => L.AddFolderAsync(dlg.SelectedPath));
    }

    async Task Rescan() { if (!_scanning) await Scan(() => L.RescanAsync()); }

    async Task Scan(Func<Task<int>> job)
    {
        _scanning = true;
        BtnAddFolder.IsEnabled = BtnRescan.IsEnabled = false;
        ScanText.Text = "Scanning…";
        try
        {
            int n = await job();
            ScanText.Text = n > 0 ? $"Added {n} song{(n == 1 ? "" : "s")}." : "Library is up to date.";
        }
        catch (Exception ex) { Paths.Log(ex); ScanText.Text = "Scan failed."; }
        finally { _scanning = false; BtnAddFolder.IsEnabled = BtnRescan.IsEnabled = true; }
    }

    async void OnFileDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (paths == null || _scanning) return;
        await Scan(async () =>
        {
            int added = 0;
            foreach (var p in paths)
            {
                if (Directory.Exists(p)) added += await L.AddFolderAsync(p);
                else if (File.Exists(p) && LibraryStore.IsAudio(p))
                {
                    bool isNew = L.Find(p) == null;
                    L.GetOrAdd(p);
                    if (isNew) added++;
                }
            }
            return added;
        });
    }

    void OnLibraryChanged()
    {
        if (_page == "Search") { RunSearchNow(); return; }
        if (_page == "Artist") { RefreshArtistLocal(); return; }
        switch (_navPage)
        {
            case "Gramophone": break;
            case "Queue": RebuildQueue(); break;
            case "Statistics": RenderStats(); break;
            default:
                _building = true;
                if (IsGroupView) RebuildGroups();
                _building = false;
                RefreshLibrary();
                break;
        }
    }

    // ================================================================== core → UI
    void OnCore(CoreChange c)
    {
        if (!IsVisible || WindowState == WindowState.Minimized) { _dirty = true; return; }
        switch (c)
        {
            case CoreChange.Position: if (_miniShown) UpdateMiniTime(false); break;
            case CoreChange.State: UpdateMiniState(); break;
            case CoreChange.Track:
                UpdateMiniTrack(); UpdateMiniToggles();
                if (_page == "Lyrics") OpenLyrics();
                if (_page == "Queue") RebuildQueue();
                break;
            case CoreChange.Queue: if (_page == "Queue") RebuildQueue(); break;
            case CoreChange.Volume: MVol.Value = C.Volume; break;
            case CoreChange.Shuffle:
            case CoreChange.Repeat: UpdateMiniToggles(); break;
            case CoreChange.Favorite:
                UpdateMiniToggles();
                if (_page == "Artist") UpdateArtistFav();
                if (_page == "Favorites") RefreshLibrary();
                break;
        }
    }

    /// <summary>While hidden or minimized the window skips UI updates; it catches up once when it comes back.</summary>
    void FlushDirty()
    {
        if (!_dirty || !IsVisible || WindowState == WindowState.Minimized) return;
        _dirty = false;
        UpdateMini();
        if (_page == "Queue") RebuildQueue();
        else if (_page == "Favorites") RefreshLibrary();
    }

    void OnNotice(string glyph, string text) => Toast(glyph, text);

    // ================================================================== mini player
    void SetMini(bool show)
    {
        if (_miniShown == show) return;
        _miniShown = show;
        if (show) UpdateMini();
        double to = show ? 80 : 0;
        if (S.Animations == AnimMode.Off) { MiniHost.BeginAnimation(HeightProperty, null); MiniHost.Height = to; return; }
        var a = new DoubleAnimation(MiniHost.Height, to, TimeSpan.FromMilliseconds(S.Animations == AnimMode.Full ? 240 : 120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        MiniHost.BeginAnimation(HeightProperty, a);
    }

    void UpdateMini()
    {
        UpdateMiniTrack(); UpdateMiniState(); UpdateMiniToggles(); UpdateMiniTime(true);
        MVol.Value = C.Volume;
    }

    void UpdateMiniTrack()
    {
        var t = C.Current;
        MiniTitle.Text = t?.Title ?? "Nothing playing";
        MiniArtist.Text = t?.Artist ?? "Pick a song from the library";
        if (C.Artwork != null)
        {
            MiniArt.Background = new ImageBrush(C.Artwork) { Stretch = Stretch.UniformToFill };
            MiniNote.Visibility = Visibility.Collapsed;
        }
        else
        {
            MiniArt.SetResourceReference(Border.BackgroundProperty, "Surface2");
            MiniNote.Visibility = Visibility.Visible;
        }
        UpdateMiniTime(true);
    }

    void UpdateMiniState() => MPlay.Content = C.State == PlayState.Playing ? "\uE769" : "\uE768";

    /// <summary>The bar moves every tick, the clock text only when the displayed second changes.</summary>
    void UpdateMiniTime(bool force)
    {
        double pos = C.Position.TotalSeconds, dur = C.Duration.TotalSeconds;
        MSeek.Value = dur > 0 ? pos / dur : 0;
        int sec = (int)pos;
        if (force || sec != _lastSec)
        {
            _lastSec = sec;
            MTimeCur.Text = Fmt.Time(pos);
            MTimeTot.Text = Fmt.Time(dur);
        }
    }

    /// <summary>Active modes are filled pills; repeat-one also swaps its glyph.</summary>
    void UpdateMiniToggles()
    {
        // colours are bound by resource key, so they keep following the theme without being re-applied
        static void Look(Control b, bool on, string tip)
        {
            if (on) b.SetResourceReference(Control.BackgroundProperty, "Accent"); else b.Background = Brushes.Transparent;
            b.SetResourceReference(Control.ForegroundProperty, on ? "OnAccent" : "Fg");
            b.ToolTip = tip;
        }
        Look(MShuffle, C.Shuffle, "Shuffle: " + (C.Shuffle ? "on" : "off"));
        MRepeat.Content = C.Repeat == RepeatMode.One ? "\uE8ED" : "\uE8EE";
        Look(MRepeat, C.Repeat != RepeatMode.Off, C.Repeat switch { RepeatMode.All => "Repeat: all songs", RepeatMode.One => "Repeat: this song", _ => "Repeat: off" });

        bool fav = C.Current?.IsFavorite == true;
        MFav.Content = fav ? "\uEB52" : "\uE006";
        MFav.SetResourceReference(Control.ForegroundProperty, fav ? "Accent" : "Fg");
        MFav.ToolTip = fav ? "Remove from favorites" : "Add to favorites";
    }

    // ================================================================== toasts
    /// <summary>Small non-blocking notification: slides up, fades after ~2.3 s. Never takes focus or blocks input.</summary>
    public void Toast(string glyph, string text)
    {
        if (!IsVisible || !S.Toasts) return;
        var mode = S.Animations;

        // the glyph decides the tone: "!" problem, "✓" success, "⟳" busy, everything else is the theme accent
        Brush tone = glyph == "!" ? new SolidColorBrush(Color.FromRgb(0xF5, 0x6B, 0x5E))
                   : glyph == "✓" ? new SolidColorBrush(Color.FromRgb(0x2D, 0xD4, 0x8F))
                   : null;

        var tb = new Border { MinWidth = 220, MaxWidth = 420, Margin = new Thickness(0, 6, 8, 8), Opacity = 0, BorderThickness = new Thickness(2) };
        tb.SetResourceReference(Border.BorderThicknessProperty, "BorderThin");
        tb.SetResourceReference(UIElement.EffectProperty, "CardShadow");
        tb.SetResourceReference(Border.BackgroundProperty, "Surface2");
        tb.SetResourceReference(Border.BorderBrushProperty, "Border");
        tb.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        var move = new TranslateTransform(0, mode == AnimMode.Full ? 16 : 0);
        tb.RenderTransform = move;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var strip = new Border();
        if (tone != null) strip.Background = tone; else strip.SetResourceReference(Border.BackgroundProperty, "AccentGradient");
        grid.Children.Add(strip);

        var chip = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var chipFill = new Border { CornerRadius = new CornerRadius(14), Opacity = 0.2 };
        var g = new TextBlock { Text = glyph, FontSize = 14, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (tone != null) { chipFill.Background = tone; g.Foreground = tone; }
        else { chipFill.SetResourceReference(Border.BackgroundProperty, "Accent"); g.SetResourceReference(TextBlock.ForegroundProperty, "Accent"); }
        var cg = new Grid(); cg.Children.Add(chipFill); cg.Children.Add(g);
        chip.Child = cg;
        Grid.SetColumn(chip, 1);
        grid.Children.Add(chip);

        var t = new TextBlock { Text = text, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 11, 16, 11) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        Grid.SetColumn(t, 2);
        grid.Children.Add(t);
        tb.Child = grid;

        while (ToastHost.Children.Count >= 3) ToastHost.Children.RemoveAt(0);
        ToastHost.Children.Add(tb);

        if (mode == AnimMode.Off) tb.Opacity = 1;
        else
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            tb.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            if (mode == AnimMode.Full) move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Clamp(1800 + text.Length * 45, 2300, 6000)) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            void Remove() => ToastHost.Children.Remove(tb);
            if (mode == AnimMode.Off) { Remove(); return; }
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260));
            fade.Completed += (_, _) => Remove();
            tb.BeginAnimation(OpacityProperty, fade);
            if (mode == AnimMode.Full) move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -8, TimeSpan.FromMilliseconds(260)));
        };
        timer.Start();
    }
}

/// <summary>Minimal modal text prompt that follows the Main Window theme.</summary>
public static class Prompt
{
    public static string Ask(Window owner, string title, string label) =>
        Dialogs.Ask(owner, title, label, "", "Press Enter to confirm, Esc to cancel.", "Create");
}
