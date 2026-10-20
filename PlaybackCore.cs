using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PortablePlayer;

/// <summary>
/// Single owner of audio + playback state. Main player, desktop panel, tray and system media controls
/// only call its methods and listen to <see cref="Changed"/>; none of them has its own audio engine.
/// </summary>
public sealed class PlaybackCore
{
    readonly AppSettings _s;
    readonly LibraryStore _lib;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    static readonly Random Rng = new();
    static string QueueFile => Path.Combine(Paths.DataDir, "queue.json");

    MediaPlayer _mp;
    List<Track> _orig = new();      // un-shuffled order, used to undo shuffle
    Track _marked;
    TimeSpan? _seekAfterOpen;
    bool _autoplay, _counted, _queueDirty;
    int _failStreak, _persistTick;

    public ObservableCollection<Track> Queue { get; } = new();
    public int Index { get; private set; } = -1;
    public PlayState State { get; private set; } = PlayState.NoMedia;
    public string ErrorText { get; private set; }
    public TimeSpan Duration { get; private set; }
    public double Volume { get; private set; }
    public bool Shuffle { get; private set; }
    public RepeatMode Repeat { get; private set; }
    public BitmapSource Artwork { get; private set; }
    public Palette Palette { get; private set; } = Palette.Default;
    public Track Current => Index >= 0 && Index < Queue.Count ? Queue[Index] : null;

    public event Action<CoreChange> Changed;
    /// <summary>Short user-facing messages (glyph, text) for toast notifications.</summary>
    public event Action<string, string> Notice;
    void Say(string glyph, string text) => Notice?.Invoke(glyph, text);

    public TimeSpan Position => State switch
    {
        PlayState.Loading => _seekAfterOpen ?? TimeSpan.Zero,
        PlayState.Playing or PlayState.Paused => _mp.Position,
        _ => TimeSpan.Zero
    };

    public PlaybackCore(AppSettings s, LibraryStore lib)
    {
        _s = s; _lib = lib;
        Volume = s.Volume; Shuffle = s.Shuffle; Repeat = s.Repeat;
        _timer.Tick += (_, _) => OnTick();
        CreatePlayer();
    }

    void Raise(CoreChange c) => Changed?.Invoke(c);

    // ------------------------------------------------------------------ engine
    void CreatePlayer()
    {
        _mp = new MediaPlayer { Volume = Volume };
        _mp.MediaOpened += Mp_Opened;
        _mp.MediaEnded += Mp_Ended;
        _mp.MediaFailed += Mp_Failed;
    }

    void DestroyPlayer()
    {
        if (_mp == null) return;
        _mp.MediaOpened -= Mp_Opened;
        _mp.MediaEnded -= Mp_Ended;
        _mp.MediaFailed -= Mp_Failed;
        try { _mp.Close(); } catch { }
        _mp = null;
    }

    /// <summary>Recovery: rebuild the audio engine and reload the current track.</summary>
    public void Recover()
    {
        try
        {
            var cur = Current;
            _timer.Stop();
            DestroyPlayer();
            CreatePlayer();
            _failStreak = 0;
            if (cur != null) Load(cur, true, null);
            else SetState(PlayState.NoMedia);
        }
        catch (Exception ex)
        {
            Paths.Log(ex);
            ErrorText = ex.Message;
            SetState(PlayState.Error);
        }
    }

    void SetState(PlayState s)
    {
        State = s;
        if (s == PlayState.Playing) _timer.Start(); else _timer.Stop();
        Raise(CoreChange.State);
    }

    void Load(Track t, bool autoplay, TimeSpan? seek)
    {
        _autoplay = autoplay; _seekAfterOpen = seek; _counted = false;
        Duration = TimeSpan.FromSeconds(t.DurationSec);
        if (_marked != null) _marked.IsCurrent = false;
        t.IsCurrent = true; _marked = t;

        var (art, pal) = ArtworkService.Get(t);
        Artwork = art; Palette = pal;
        ErrorText = null;
        SetState(PlayState.Loading);
        Raise(CoreChange.Track);
        try
        {
            if (!t.IsOnline && !File.Exists(t.FilePath)) throw new FileNotFoundException("File not found");
            _mp.Open(new Uri(t.FilePath));          // a local file or an online preview URL
        }
        catch (Exception ex)
        {
            string msg = ex.Message;
            Application.Current.Dispatcher.BeginInvoke(new Action(() => Failed(msg)));
        }
        Persist();
    }

    void Mp_Opened(object s, EventArgs e)
    {
        _failStreak = 0;
        if (_mp.NaturalDuration.HasTimeSpan)
        {
            Duration = _mp.NaturalDuration.TimeSpan;
            if (Current != null) Current.DurationSec = Duration.TotalSeconds;
        }
        if (_seekAfterOpen is TimeSpan p && p < Duration) _mp.Position = p;
        _seekAfterOpen = null;
        if (_autoplay) { _mp.Play(); SetState(PlayState.Playing); }
        else SetState(PlayState.Paused);
        Raise(CoreChange.Position);
    }

    void Mp_Ended(object s, EventArgs e)
    {
        if (Repeat == RepeatMode.One)
        {
            _counted = false;
            _mp.Position = TimeSpan.Zero;
            _mp.Play();
            Raise(CoreChange.Position);
        }
        else Advance(true);
    }

    void Mp_Failed(object s, ExceptionEventArgs e) => Failed(e.ErrorException?.Message);

    void Failed(string msg)
    {
        _failStreak++;
        ErrorText = string.IsNullOrWhiteSpace(msg) ? "This file cannot be played" : msg;
        if (Current != null)
            Say("!", Current.IsOnline ? "The preview could not be played." : $"Can't play “{Current.Title}”: {ErrorText}");
        // when playing through a queue, skip unplayable files (bounded, so it can never loop forever)
        if (_autoplay && Queue.Count > 1 && _failStreak < Queue.Count) { Advance(true); return; }
        SetState(PlayState.Error);
    }

    void OnTick()
    {
        Raise(CoreChange.Position);
        var cur = Current;
        if (!_counted && cur != null && !cur.IsOnline)
        {
            double p = Position.TotalSeconds, d = Duration.TotalSeconds;
            if (p >= 30 || (d > 0 && p >= d * 0.5))
            {
                _counted = true;
                cur.PlayCount++;
                cur.LastPlayed = DateTime.Now;
                _lib.SaveSoon();
            }
        }
        if (++_persistTick >= 40) { _persistTick = 0; Persist(); }
    }

    // ------------------------------------------------------------------ transport
    public void Play()
    {
        switch (State)
        {
            case PlayState.Error: Recover(); break;
            case PlayState.Paused:
            case PlayState.Stopped:
                _mp.Play(); SetState(PlayState.Playing); break;
            case PlayState.Loading: _autoplay = true; break;
            case PlayState.NoMedia:
                if (_lib.Tracks.Count > 0) PlayTracks(LibraryStore.Sort(_lib.Tracks).ToList(), 0);
                break;
        }
    }

    public void Pause()
    {
        if (State != PlayState.Playing) return;
        _mp.Pause();
        SetState(PlayState.Paused);
        Persist();
    }

    public void Toggle()
    {
        if (State == PlayState.Playing) Pause(); else Play();
    }

    public void Stop()
    {
        if (Current == null) return;
        _mp.Stop();
        SetState(PlayState.Stopped);
        Raise(CoreChange.Position);
        Persist();
    }

    public void Next() => Advance(false);

    void Advance(bool auto)
    {
        if (Queue.Count == 0) return;
        int n = Index + 1;
        if (n >= Queue.Count)
        {
            if (auto && Repeat == RepeatMode.Off) { Stop(); return; }
            n = 0;
        }
        Index = n;
        Load(Queue[n], true, null);
    }

    public void Previous()
    {
        if (Queue.Count == 0) return;
        if (Position.TotalSeconds > 3) { SeekTo(TimeSpan.Zero); return; }
        int n = Index - 1;
        if (n < 0) n = Repeat == RepeatMode.All ? Queue.Count - 1 : 0;
        Index = n;
        Load(Queue[n], true, null);
    }

    public void PlayIndex(int i)
    {
        if (i < 0 || i >= Queue.Count) return;
        Index = i;
        Load(Queue[i], true, null);
    }

    public void Seek(double fraction)
    {
        if (Duration.TotalSeconds <= 0) return;
        SeekTo(TimeSpan.FromSeconds(Duration.TotalSeconds * Math.Clamp(fraction, 0, 1)));
    }

    void SeekTo(TimeSpan t)
    {
        if (State is not (PlayState.Playing or PlayState.Paused)) return;
        _mp.Position = t;
        Raise(CoreChange.Position);
    }

    public void SetVolume(double v)
    {
        Volume = Math.Clamp(v, 0, 1);
        if (_mp != null) _mp.Volume = Volume;
        _s.Volume = Volume; _s.SaveSoon();
        Raise(CoreChange.Volume);
    }

    // ------------------------------------------------------------------ queue
    public void PlayTracks(IList<Track> list, int start, string playlistName = null)
    {
        if (list == null || list.Count == 0) return;
        start = Math.Clamp(start, 0, list.Count - 1);
        _orig = list.ToList();
        _s.CurrentPlaylist = playlistName;
        var order = list.ToList();
        var first = order[start];
        if (Shuffle)
        {
            order.RemoveAt(start);
            Shuf(order);
            order.Insert(0, first);
            start = 0;
        }
        SetQueue(order, start);
        Load(first, true, null);
    }

    void SetQueue(List<Track> order, int index)
    {
        Queue.Clear();
        foreach (var t in order) Queue.Add(t);
        Index = index;
        _queueDirty = true;
        Raise(CoreChange.Queue);
    }

    public void Enqueue(IEnumerable<Track> tracks)
    {
        var l = tracks.ToList();
        if (l.Count == 0) return;
        if (Queue.Count == 0) { PlayTracks(l, 0); return; }
        foreach (var t in l) { Queue.Add(t); _orig.Add(t); }
        _queueDirty = true;
        Raise(CoreChange.Queue);
        Persist();
        Say("＋", l.Count == 1 ? "Added to Queue" : $"Added {l.Count} songs to Queue");
    }

    public void EnqueueNext(IEnumerable<Track> tracks)
    {
        var l = tracks.ToList();
        if (l.Count == 0) return;
        if (Queue.Count == 0) { PlayTracks(l, 0); return; }
        int at = Index + 1;
        foreach (var t in l) { Queue.Insert(at++, t); _orig.Add(t); }
        _queueDirty = true;
        Raise(CoreChange.Queue);
        Persist();
        Say("▶", l.Count == 1 ? "Playing next" : $"{l.Count} songs play next");
    }

    /// <summary>Inserts tracks at a queue position (never before the current track).</summary>
    public void InsertIntoQueue(IEnumerable<Track> tracks, int at)
    {
        var l = tracks.ToList();
        if (l.Count == 0) return;
        if (Queue.Count == 0) { PlayTracks(l, 0); return; }
        at = Math.Clamp(at, Index + 1, Queue.Count);
        foreach (var t in l) { Queue.Insert(at++, t); _orig.Add(t); }
        _queueDirty = true;
        Raise(CoreChange.Queue);
        Persist();
        Say("＋", l.Count == 1 ? "Added to Queue" : $"Added {l.Count} songs to Queue");
    }

    /// <summary>The current track's metadata or cover changed: re-read the artwork and tell every view.</summary>
    public void RefreshCurrent()
    {
        var t = Current;
        if (t == null) return;
        var (art, pal) = ArtworkService.Get(t);
        Artwork = art; Palette = pal;
        Raise(CoreChange.Track);
    }

    public void RemoveFromQueue(int i)
    {
        if (i < 0 || i >= Queue.Count) return;
        var t = Queue[i];
        Queue.RemoveAt(i);
        _orig.Remove(t);
        _queueDirty = true;
        if (i < Index) Index--;
        else if (i == Index)
        {
            if (Queue.Count == 0)
            {
                Index = -1;
                try { _mp.Close(); } catch { }
                Duration = TimeSpan.Zero; Artwork = null; Palette = Palette.Default;
                if (_marked != null) { _marked.IsCurrent = false; _marked = null; }
                SetState(PlayState.NoMedia);
                Raise(CoreChange.Track);
            }
            else
            {
                Index = Math.Min(i, Queue.Count - 1);
                Load(Queue[Index], State == PlayState.Playing, null);
            }
        }
        Raise(CoreChange.Queue);
        Persist();
        Say("✕", "Removed from Queue");
    }

    public void MoveInQueue(int from, int to)
    {
        if (from < 0 || from >= Queue.Count || to < 0 || to >= Queue.Count || from == to) return;
        Queue.Move(from, to);
        if (Index == from) Index = to;
        else if (from < Index && to >= Index) Index--;
        else if (from > Index && to <= Index) Index++;
        _queueDirty = true;
        Raise(CoreChange.Queue);
        Persist();
    }

    public void ClearUpcoming()
    {
        if (Index < 0 || Index >= Queue.Count - 1) return;
        while (Queue.Count > Index + 1) { _orig.Remove(Queue[^1]); Queue.RemoveAt(Queue.Count - 1); }
        _queueDirty = true;
        Raise(CoreChange.Queue);
        Persist();
        Say("✕", "Cleared Up Next");
    }

    static void Shuf(List<Track> l)
    {
        for (int i = l.Count - 1; i > 0; i--)
        {
            int j = Rng.Next(i + 1);
            (l[i], l[j]) = (l[j], l[i]);
        }
    }

    public void SetShuffle(bool on)
    {
        if (on == Shuffle) return;
        Shuffle = on;
        var cur = Current;
        if (Queue.Count > 0 && cur != null)
        {
            List<Track> order; int idx;
            if (on)
            {
                // keep what was already played, shuffle only what is still to come
                var before = Queue.Take(Index + 1).ToList();
                var after = Queue.Skip(Index + 1).ToList();
                Shuf(after);
                order = before.Concat(after).ToList(); idx = Index;
            }
            else
            {
                order = _orig.ToList(); idx = order.IndexOf(cur);
                if (idx < 0) { order = Queue.ToList(); idx = Index; }
            }
            SetQueue(order, idx);
        }
        Raise(CoreChange.Shuffle);
        Persist();
    }

    public void SetRepeat(RepeatMode m)
    {
        Repeat = m;
        Raise(CoreChange.Repeat);
        Persist();
    }

    public void CycleRepeat() => SetRepeat((RepeatMode)(((int)Repeat + 1) % 3));

    public void SetFavorite(IEnumerable<Track> tracks, bool fav)
    {
        int n = 0;
        foreach (var t in tracks) { t.IsFavorite = fav; n++; }
        _lib.SaveSoon();
        Raise(CoreChange.Favorite);
        if (n > 0)
            Say(fav ? "♥" : "♡", fav ? (n == 1 ? "Added to Favorites" : $"Added {n} songs to Favorites")
                                     : (n == 1 ? "Removed from Favorites" : $"Removed {n} songs from Favorites"));
    }

    // ------------------------------------------------------------------ persistence
    public void Persist()
    {
        var cur = Current;
        _s.CurrentTrack = cur != null && !cur.IsOnline ? Paths.ToStored(cur.FilePath) : null;
        _s.PositionSec = Position.TotalSeconds;
        _s.QueueIndex = Index;
        _s.Volume = Volume; _s.Shuffle = Shuffle; _s.Repeat = Repeat;
        if (_queueDirty)
        {
            _queueDirty = false;
            try { Paths.WriteAtomic(QueueFile, JsonSerializer.Serialize(Queue.Where(t => !t.IsOnline).Select(t => Paths.ToStored(t.FilePath)).ToList())); }
            catch (Exception ex) { Paths.Log(ex); }
        }
        _s.Save();
    }

    /// <summary>Restores queue, current track and position (paused) after a restart.</summary>
    public void Restore()
    {
        try
        {
            if (!File.Exists(QueueFile)) return;
            var paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(QueueFile)) ?? new();
            var list = new List<Track>();
            foreach (var sp in paths)
            {
                string p = Paths.FromStored(sp);
                var t = _lib.Find(p) ?? (File.Exists(p) ? LibraryStore.ReadTrack(p) : null);
                if (t != null) list.Add(t);
            }
            if (list.Count == 0) return;

            int idx = Math.Clamp(_s.QueueIndex, 0, list.Count - 1);
            if (_s.CurrentTrack != null)
            {
                var cur = _lib.Find(Paths.FromStored(_s.CurrentTrack));
                int i = cur != null ? list.IndexOf(cur) : -1;
                if (i >= 0) idx = i;
            }
            _orig = list.ToList();
            SetQueue(list, idx);
            _queueDirty = false;
            Load(list[idx], false, TimeSpan.FromSeconds(_s.PositionSec));
        }
        catch (Exception ex) { Paths.Log(ex); }
    }

    public void Shutdown()
    {
        try { Persist(); } catch { }
        _timer.Stop();
        DestroyPlayer();
    }
}
