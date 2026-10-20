using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PortablePlayer;

/// <summary>
/// Now-playing information and transport controls of the Gramophone (title, progress, shuffle / repeat, volume).
/// Fixed light-on-dark look - it is part of the Gramophone identity, not of the Main Window theme.
/// Drives itself from PlaybackCore while visible.
/// </summary>
public sealed class PlayerDeck : UserControl
{
    static readonly Brush Dim = Frozen("#A8FFFFFF");
    static readonly Brush Bright = Frozen("#F2FFFFFF");
    static readonly Brush OnFill = Frozen("#FF1B1410");
    static readonly Brush DefaultAccent = Frozen("#FFC9A35E");
    static readonly Brush TrackBrush = Frozen("#E0FFFFFF");

    static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    readonly TextBlock _title, _artist, _album, _cur, _tot, _status;
    readonly SeekBar _seek, _vol;
    readonly Button _shuffle, _prev, _play, _next, _repeat, _fav, _queue;
    Brush _accent = DefaultAccent;
    bool _attached;
    int _lastSec = -1;

    static PlaybackCore C => App.Instance.Core;

    public event Action QueueClicked;

    public Brush Accent
    {
        get => _accent;
        set { _accent = value ?? DefaultAccent; _seek.Fill = _accent; UpdateToggles(); }
    }

    public PlayerDeck()
    {
        var icon = (Style)Application.Current.FindResource("IconBtn");
        var playStyle = (Style)Application.Current.FindResource("PlayBtn");
        var shadow = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.75, Color = Colors.Black };
        shadow.Freeze();

        TextBlock T(double size, Brush fg, FontWeight? w = null, Thickness? m = null)
        {
            var t = new TextBlock { FontSize = size, Foreground = fg, TextTrimming = TextTrimming.CharacterEllipsis, Effect = shadow };
            if (w != null) t.FontWeight = w.Value;
            if (m != null) t.Margin = m.Value;
            return t;
        }

        Button B(Style st, string glyph, string tip, double size = 0)
        {
            var b = new Button { Style = st, Content = glyph, ToolTip = tip };
            if (size > 0) { b.Width = size; b.Height = size; b.FontSize = 14; }
            return b;
        }

        _title = T(17, Brushes.White, FontWeights.SemiBold);
        _artist = T(13, Frozen("#E6FFFFFF"), null, new Thickness(0, 1, 0, 0));
        _album = T(11.5, Frozen("#B8FFFFFF"), null, new Thickness(0, 1, 0, 0));

        _seek = new SeekBar { Margin = new Thickness(0, 8, 0, 0), Fill = _accent };
        _cur = T(11, Frozen("#CCFFFFFF")); _cur.Text = "0:00";
        _tot = T(11, Frozen("#CCFFFFFF")); _tot.Text = "0:00";
        _status = T(11, Frozen("#B8FFFFFF"), null, new Thickness(8, 0, 8, 0)); _status.TextAlignment = TextAlignment.Center;

        var times = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        times.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        times.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        times.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        times.Children.Add(_cur);
        Grid.SetColumn(_status, 1); times.Children.Add(_status);
        Grid.SetColumn(_tot, 2); times.Children.Add(_tot);

        _shuffle = B(icon, "\uE8B1", "Shuffle");
        _prev = B(icon, "\uE892", "Previous");
        _play = B(playStyle, "\uE768", "Play / pause");
        _play.Background = Frozen("#30FFFFFF");
        _next = B(icon, "\uE893", "Next");
        _repeat = B(icon, "\uE8EE", "Repeat");

        var transport = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var b in new[] { _shuffle, _prev, _play, _next, _repeat }) transport.Children.Add(b);

        _fav = B(icon, "\uE006", "Favorite", 32);
        _queue = B(icon, "\uE8FD", "Queue", 32);
        _queue.Margin = new Thickness(8, 0, 0, 0);
        var volIcon = new TextBlock
        {
            Text = "\uE767", FontFamily = (FontFamily)Application.Current.FindResource("Icons"), FontSize = 13,
            Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0)
        };
        _vol = new SeekBar { VerticalAlignment = VerticalAlignment.Center, Fill = TrackBrush };

        var bottom = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.Children.Add(_fav);
        Grid.SetColumn(volIcon, 1); bottom.Children.Add(volIcon);
        Grid.SetColumn(_vol, 2); bottom.Children.Add(_vol);
        Grid.SetColumn(_queue, 3); bottom.Children.Add(_queue);

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(_title); stack.Children.Add(_artist); stack.Children.Add(_album);
        stack.Children.Add(_seek); stack.Children.Add(times); stack.Children.Add(transport); stack.Children.Add(bottom);
        Content = stack;

        // wiring
        _play.Click += (_, _) => C.Toggle();
        _prev.Click += (_, _) => C.Previous();
        _next.Click += (_, _) => C.Next();
        _shuffle.Click += (_, _) => C.SetShuffle(!C.Shuffle);
        _repeat.Click += (_, _) => C.CycleRepeat();
        _fav.Click += (_, _) => { var t = C.Current; if (t != null) C.SetFavorite(new[] { t }, !t.IsFavorite); };
        _queue.Click += (_, _) => QueueClicked?.Invoke();
        _seek.Dragging += f => _cur.Text = Fmt.Time(f * C.Duration.TotalSeconds);
        _seek.Commit += f => C.Seek(f);
        _vol.Dragging += v => C.SetVolume(v);
        _vol.Commit += v => C.SetVolume(v);

        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Attach(); else Detach(); };
        Unloaded += (_, _) => Detach();
    }

    void Attach()
    {
        if (_attached || App.Instance == null) return;
        _attached = true;
        C.Changed += OnCore;
        UpdateTrack(); UpdateState(); UpdateTime(true); UpdateToggles();
        _vol.Value = C.Volume;
    }

    void Detach()
    {
        if (!_attached) return;
        _attached = false;
        C.Changed -= OnCore;
    }

    void OnCore(CoreChange c)
    {
        switch (c)
        {
            case CoreChange.Position: UpdateTime(false); break;
            case CoreChange.State: UpdateState(); break;
            case CoreChange.Track: UpdateTrack(); UpdateTime(true); UpdateToggles(); break;
            case CoreChange.Volume: _vol.Value = C.Volume; break;
            case CoreChange.Shuffle:
            case CoreChange.Repeat:
            case CoreChange.Favorite: UpdateToggles(); break;
        }
    }

    void UpdateTrack()
    {
        var t = C.Current;
        _title.Text = t?.Title ?? "Nothing playing";
        _artist.Text = t?.Artist ?? "";
        _album.Text = t?.Album ?? "";
    }

    void UpdateState()
    {
        _play.Content = C.State == PlayState.Playing ? "\uE769" : "\uE768";
        _status.Text = C.State switch
        {
            PlayState.Paused => "Paused",
            PlayState.Stopped => "Stopped",
            PlayState.Loading => "Loading…",
            PlayState.Error => "Can't play this file",
            PlayState.NoMedia => "Pick a song from the library",
            _ => ""
        };
    }

    /// <summary>The bar moves every tick; the text only changes when the displayed second changes.</summary>
    void UpdateTime(bool force)
    {
        double pos = C.Position.TotalSeconds, dur = C.Duration.TotalSeconds;
        _seek.Value = dur > 0 ? pos / dur : 0;
        int sec = (int)pos;
        if (force || sec != _lastSec)
        {
            _lastSec = sec;
            _cur.Text = Fmt.Time(pos);
            _tot.Text = Fmt.Time(dur);
        }
    }

    /// <summary>Shuffle / repeat are filled pills when active, plain glyphs when off (never just a colour change).</summary>
    void UpdateToggles()
    {
        Mode(_shuffle, C.Shuffle, _accent, "Shuffle: " + (C.Shuffle ? "on" : "off"));
        string rep = C.Repeat switch { RepeatMode.All => "Repeat: all songs", RepeatMode.One => "Repeat: this song", _ => "Repeat: off" };
        _repeat.Content = C.Repeat == RepeatMode.One ? "\uE8ED" : "\uE8EE";
        Mode(_repeat, C.Repeat != RepeatMode.Off, _accent, rep);

        bool fav = C.Current?.IsFavorite == true;
        _fav.Content = fav ? "\uEB52" : "\uE006";
        _fav.Foreground = fav ? _accent : Dim;
        _fav.ToolTip = fav ? "Remove from favorites" : "Add to favorites";
    }

    static void Mode(Button b, bool on, Brush accent, string tip)
    {
        b.Background = on ? accent : Brushes.Transparent;
        b.Foreground = on ? OnFill : Dim;
        b.ToolTip = tip;
    }
}
