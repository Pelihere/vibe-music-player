using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PortablePlayer;

public enum PlayState { NoMedia, Loading, Playing, Paused, Stopped, Error }
public enum RepeatMode { Off, All, One }
public enum PanelDock { Left, Right, Top }
public enum PanelSize { Compact, Normal, Large }
public enum AnimMode { Full, Reduced, Off }
public enum CoreChange { State, Track, Position, Volume, Shuffle, Repeat, Queue, Favorite }

public static class Fmt
{
    public static string Time(double s)
    {
        if (double.IsNaN(s) || s < 0) s = 0;
        var t = TimeSpan.FromSeconds(s);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}

public sealed class Track : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;
    void Notify([CallerMemberName] string n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    bool _fav, _cur, _thumbAsked;
    int _plays, _year, _no, _disc;
    string _title = "", _artist = "Unknown Artist", _album = "Unknown Album", _genre = "", _albumArtist = "", _cover = "";
    ImageSource _thumb;

    [JsonIgnore] public string FilePath { get; set; } = "";
    /// <summary>Serialized (possibly relative) path.</summary>
    public string P { get => Paths.ToStored(FilePath); set => FilePath = Paths.FromStored(value); }

    public string Title { get => _title; set { if (_title == value) return; _title = value ?? ""; Notify(); } }
    public string Artist { get => _artist; set { if (_artist == value) return; _artist = value ?? ""; Notify(); } }
    public string Album { get => _album; set { if (_album == value) return; _album = value ?? ""; Notify(); } }
    public string AlbumArtist { get => _albumArtist; set { if (_albumArtist == value) return; _albumArtist = value ?? ""; Notify(); } }
    public string Genre { get => _genre; set { if (_genre == value) return; _genre = value ?? ""; Notify(); } }
    public int Year { get => _year; set { if (_year == value) return; _year = value; Notify(); } }
    public int No { get => _no; set { if (_no == value) return; _no = value; Notify(); } }
    public int Disc { get => _disc; set { if (_disc == value) return; _disc = value; Notify(); } }
    public double DurationSec { get; set; }
    public bool IsFavorite { get => _fav; set { if (_fav == value) return; _fav = value; Notify(); } }
    public int PlayCount { get => _plays; set { if (_plays == value) return; _plays = value; Notify(); } }
    public DateTime? LastPlayed { get; set; }
    public DateTime Added { get; set; }

    /// <summary>Custom cover chosen in the metadata editor (stored relative to the data folder).</summary>
    public string Cover { get => _cover; set { if (_cover == value) return; _cover = value ?? ""; Notify(); } }
    /// <summary>The user edited this track in Vibe; the values live in the library even if the file could not be written.</summary>
    public bool Edited { get; set; }
    /// <summary>Edited values still have to be written into the audio file (it was locked, e.g. while playing).</summary>
    public bool PendingWrite { get; set; }

    /// <summary>An online preview (30 s) played through the normal player. Never saved in the library or the queue file.</summary>
    [JsonIgnore] public bool IsOnline { get; set; }
    [JsonIgnore] public BitmapSource OnlineArt { get; set; }
    [JsonIgnore] public string OnlineUrl { get; set; }

    [JsonIgnore] public string CoverFile => string.IsNullOrEmpty(Cover) ? null : Paths.FromStored(Cover);
    [JsonIgnore] public bool IsCurrent { get => _cur; set { if (_cur == value) return; _cur = value; Notify(); } }
    [JsonIgnore] public string Marker => IsCurrent ? "▶" : "";
    [JsonIgnore] public string Time => Fmt.Time(DurationSec);
    [JsonIgnore] public string Heart => IsFavorite ? "♥" : "";
    [JsonIgnore] public string Plays => PlayCount > 0 ? PlayCount.ToString() : "";

    /// <summary>Small cover thumbnail, loaded lazily (and only for rows that are actually on screen).</summary>
    [JsonIgnore]
    public ImageSource Thumb
    {
        get
        {
            if (!_thumbAsked) { _thumbAsked = true; ThumbService.Request(this); }
            return _thumb;
        }
        set { _thumb = value; Notify(); }
    }

    /// <summary>Forget the cached thumbnail so the next read loads the (changed) cover.</summary>
    public void ResetThumb() { _thumbAsked = false; _thumb = null; Notify(nameof(Thumb)); }
}

/// <summary>One upcoming entry in the queue view (QueueIndex = position inside PlaybackCore.Queue).</summary>
public sealed class QueueEntry
{
    public Track Track { get; init; }
    public int QueueIndex { get; init; }
    public int Number { get; init; }
    public string NumberText => Number.ToString("00");
}

/// <summary>
/// One row of a song list on the artist page. It is either a file from the library (LOCAL) or a song known only online (ONLINE).
/// Local rows show the library's own metadata and capitalization.
/// </summary>
public sealed class PopularItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;
    void Notify([CallerMemberName] string n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    string _badge = "";
    ImageSource _remoteThumb;

    public Track Track { get; init; }                 // set for LOCAL rows
    public RemoteTrack Remote { get; init; }          // set when an online source knows the song (local rows may have it too)
    public int Rank { get; init; }
    public string RankText => Rank.ToString("00");
    public bool IsLocal => Track != null;

    public string Title => Track?.Title ?? Remote?.Title ?? "";
    public string Artist => Track?.Artist ?? Remote?.Artist ?? "";
    public string Album => Track?.Album ?? Remote?.Album ?? "";
    public string Time => Track != null ? Track.Time : Remote?.DurationSec > 0 ? Fmt.Time(Remote.DurationSec) : "";
    public string Plays { get; init; } = "";
    public string Sub => string.Join("  ·  ", new[] { Album, Plays }.Where(x => !string.IsNullOrWhiteSpace(x)));

    /// <summary>LOCAL | ONLINE · PREVIEW | ONLINE | NO AUDIO SOURCE</summary>
    public string Badge { get => _badge; set { if (_badge == value) return; _badge = value; Notify(); } }

    /// <summary>Cover of an online song (loaded lazily); local rows use Track.Thumb instead.</summary>
    public ImageSource RemoteThumb { get => _remoteThumb; set { _remoteThumb = value; Notify(); } }
}

/// <summary>Artist / album / genre / playlist entry in the browse lists and search results.</summary>
public sealed class GroupItem
{
    public string Name { get; init; }
    public int Count { get; init; }
    public Track Sample { get; init; }
    public string Sub => Count == 1 ? "1 song" : $"{Count} songs";
    public string Display => $"{Name}  ({Count})";
}

public sealed class AppSettings
{
    // ---- playback persistence (queue itself lives in queue.json) ----
    public string CurrentTrack { get; set; }
    public double PositionSec { get; set; }
    public int QueueIndex { get; set; }
    public string CurrentPlaylist { get; set; }
    public double Volume { get; set; } = 0.7;
    public bool Shuffle { get; set; }
    public RepeatMode Repeat { get; set; } = RepeatMode.Off;

    // ---- desktop panel ----
    public bool PanelEnabled { get; set; } = true;
    public PanelDock Dock { get; set; } = PanelDock.Right;
    public int? PanelX { get; set; }
    public int? PanelY { get; set; }
    public PanelSize Size { get; set; } = PanelSize.Normal;
    public double Transparency { get; set; } = 0.85;   // panel opacity 0.2 .. 1.0
    public bool Locked { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool AutoHide { get; set; }
    public int AutoHideSeconds { get; set; } = 6;
    public bool DynamicTheme { get; set; } = true;
    public AnimMode Animations { get; set; } = AnimMode.Full;
    /// <summary>Musical hover effect: Off | Slow | Medium | Fast (speed of the flowing notes).</summary>
    public string ScoreEffect { get; set; } = "Slow";

    // ---- main window ----
    public string Theme { get; set; } = "Dark Teal";
    public string StartPage { get; set; } = "Library";          // Library | Gramophone | Favorites | Artists | Albums | Playlists
    public string Density { get; set; } = "Comfortable";        // Comfortable | Compact
    public bool Toasts { get; set; } = true;
    public string LibrarySort { get; set; } = "Default";        // Default | Title | Artist | Album | Added | Duration
    public bool LibrarySortDesc { get; set; }

    // ---- online artist info / metadata ----
    public bool OnlineArtistInfo { get; set; } = true;
    public string LastFmApiKey { get; set; } = "";
    /// <summary>Optional official SoundCloud API app credentials (soundcloud.com/you/apps). Without them SoundCloud is simply not used.</summary>
    public string SoundCloudClientId { get; set; } = "";
    public string SoundCloudClientSecret { get; set; } = "";
    public bool WriteTagsToFiles { get; set; } = true;

    // ---- application ----
    public bool StartWithWindows { get; set; }
    public bool OpenPlayerOnWindowsStart { get; set; }
    public Dictionary<string, string> Hotkeys { get; set; } = new();

    static string FilePath => Path.Combine(Paths.DataDir, "settings.json");
    DispatcherTimer _debounce;

    public static AppSettings Load()
    {
        AppSettings s = null;
        try { if (File.Exists(FilePath)) s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Paths.Json); }
        catch (Exception ex) { Paths.Log(ex); }
        s ??= new AppSettings();
        s.Normalize();
        return s;
    }

    void Normalize()
    {
        foreach (var a in HotkeyService.Actions)
            if (!Hotkeys.ContainsKey(a.Key)) Hotkeys[a.Key] = a.Default;
        Transparency = Math.Clamp(Transparency, 0.2, 1.0);
        AutoHideSeconds = Math.Clamp(AutoHideSeconds, 2, 120);
        Volume = Math.Clamp(Volume, 0, 1);
        if (string.IsNullOrWhiteSpace(Theme)) Theme = "Dark Teal";
        if (ScoreEffect is not ("Off" or "Slow" or "Medium" or "Fast")) ScoreEffect = "Slow";
        if (Density != "Compact") Density = "Comfortable";
        if (string.IsNullOrWhiteSpace(StartPage)) StartPage = "Library";
        if (string.IsNullOrWhiteSpace(LibrarySort)) LibrarySort = "Default";
        LastFmApiKey = (LastFmApiKey ?? "").Trim();
        SoundCloudClientId = (SoundCloudClientId ?? "").Trim();
        SoundCloudClientSecret = (SoundCloudClientSecret ?? "").Trim();
    }

    public void Save()
    {
        try { Paths.WriteAtomic(FilePath, JsonSerializer.Serialize(this, Paths.Json)); }
        catch (Exception ex) { Paths.Log(ex); }
    }

    /// <summary>Debounced save for slider-style changes.</summary>
    public void SaveSoon()
    {
        if (_debounce == null)
        {
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _debounce.Tick += (_, _) => { _debounce.Stop(); Save(); };
        }
        _debounce.Stop();
        _debounce.Start();
    }
}
