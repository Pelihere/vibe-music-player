using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace PortablePlayer;

public sealed class LibraryData
{
    public List<string> Folders { get; set; } = new();
    public List<Track> Tracks { get; set; } = new();
    /// <summary>Playlist name -> stored (relative if possible) file paths.</summary>
    public Dictionary<string, List<string>> Playlists { get; set; } = new();
}

public sealed class LibraryStore
{
    static readonly HashSet<string> Ext = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".flac", ".wav", ".m4a", ".aac", ".wma", ".aif", ".aiff" };

    static string FilePath => Path.Combine(Paths.DataDir, "library.json");

    LibraryData _d = new();
    readonly Dictionary<string, Track> _byPath = new(StringComparer.OrdinalIgnoreCase);
    DispatcherTimer _debounce;

    public event Action Changed;
    public IReadOnlyList<Track> Tracks => _d.Tracks;
    public IReadOnlyList<string> Folders => _d.Folders;
    public IEnumerable<string> PlaylistNames => _d.Playlists.Keys.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase);

    public static bool IsAudio(string path) => Ext.Contains(Path.GetExtension(path));

    public static IEnumerable<Track> Sort(IEnumerable<Track> src) =>
        src.OrderBy(t => t.Artist, StringComparer.CurrentCultureIgnoreCase)
           .ThenBy(t => t.Album, StringComparer.CurrentCultureIgnoreCase)
           .ThenBy(t => t.No)
           .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase);

    public static LibraryStore Load()
    {
        var s = new LibraryStore();
        try
        {
            if (File.Exists(FilePath))
                s._d = JsonSerializer.Deserialize<LibraryData>(File.ReadAllText(FilePath), Paths.Json) ?? new LibraryData();
        }
        catch (Exception ex) { Paths.Log(ex); }
        // playlist names are matched case-insensitively ("Road Trip" == "road trip"), spelling is kept as typed
        s._d.Playlists = new Dictionary<string, List<string>>(s._d.Playlists ?? new(), StringComparer.CurrentCultureIgnoreCase);
        foreach (var t in s._d.Tracks)
            if (t.Added == default) t.Added = FileStamp(t.FilePath);
        s.Index();
        return s;
    }

    static DateTime FileStamp(string path)
    {
        try { if (File.Exists(path)) return File.GetCreationTimeUtc(path); } catch { }
        return DateTime.UtcNow;
    }

    /// <summary>Call after a track's metadata changed: refreshes every view and persists the library.</summary>
    public void Touch(Track t = null)
    {
        SaveSoon();
        Changed?.Invoke();
    }

    void Index()
    {
        _byPath.Clear();
        foreach (var t in _d.Tracks) _byPath[t.FilePath] = t;
    }

    public Track Find(string path) => path != null && _byPath.TryGetValue(path, out var t) ? t : null;

    public void Save()
    {
        try { Paths.WriteAtomic(FilePath, JsonSerializer.Serialize(_d, Paths.Json)); }
        catch (Exception ex) { Paths.Log(ex); }
    }

    public void SaveSoon()
    {
        if (_debounce == null)
        {
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _debounce.Tick += (_, _) => { _debounce.Stop(); Save(); };
        }
        _debounce.Stop();
        _debounce.Start();
    }

    public static Track ReadTrack(string path)
    {
        var t = new Track { FilePath = path, Title = Path.GetFileNameWithoutExtension(path), Added = DateTime.UtcNow };
        try
        {
            using var f = TagLib.File.Create(path);
            var tag = f.Tag;
            if (!string.IsNullOrWhiteSpace(tag.Title)) t.Title = tag.Title.Trim();
            string artist = tag.FirstPerformer ?? tag.FirstAlbumArtist;
            if (!string.IsNullOrWhiteSpace(artist)) t.Artist = artist.Trim();
            if (!string.IsNullOrWhiteSpace(tag.Album)) t.Album = tag.Album.Trim();
            t.Genre = tag.FirstGenre ?? "";
            t.Year = (int)tag.Year;
            t.No = (int)tag.Track;
            t.Disc = (int)tag.Disc;
            t.AlbumArtist = (tag.FirstAlbumArtist ?? "").Trim();
            t.DurationSec = f.Properties.Duration.TotalSeconds;
        }
        catch { /* unreadable tags: keep file-name based defaults */ }
        SplitArtistTitle(t);
        return t;
    }

    /// <summary>
    /// Downloads from SoundCloud and similar sites often come as "Artist - Title" with no artist tag (or the artist repeated in the title).
    /// Only for newly added songs: no artist -> take it from the title; artist already in the title -> remove it from the title.
    /// </summary>
    static void SplitArtistTitle(Track t)
    {
        var m = System.Text.RegularExpressions.Regex.Match(t.Title ?? "", @"^\s*(?<a>.{1,60}?)\s+[-\u2013\u2014]\s+(?<t>.+)$");
        if (!m.Success) return;
        string left = m.Groups["a"].Value.Trim(), right = m.Groups["t"].Value.Trim();
        if (left.Length == 0 || right.Length == 0) return;
        bool noArtist = string.IsNullOrWhiteSpace(t.Artist) || t.Artist == "Unknown Artist";
        if (noArtist) { t.Artist = left; t.Title = right; }
        else if (Cmp.SameArtist(left, t.Artist)) t.Title = right;
    }

    public Track GetOrAdd(string path)
    {
        var t = Find(path);
        if (t != null) return t;
        t = ReadTrack(path);
        _d.Tracks.Add(t);
        _byPath[path] = t;
        SaveSoon();
        Changed?.Invoke();
        return t;
    }

    public Task<int> AddFolderAsync(string folder)
    {
        if (!_d.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) _d.Folders.Add(folder);
        return ScanAsync(new[] { folder });
    }

    public Task<int> RescanAsync() => ScanAsync(_d.Folders.ToList());

    async Task<int> ScanAsync(IList<string> roots)
    {
        var known = new HashSet<string>(_d.Tracks.Select(t => t.FilePath), StringComparer.OrdinalIgnoreCase);
        var result = await Task.Run(() =>
        {
            var added = new List<Track>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var file in Directory.EnumerateFiles(root, "*", opt))
                {
                    if (!Ext.Contains(Path.GetExtension(file))) continue;
                    seen.Add(file);
                    if (!known.Contains(file)) added.Add(ReadTrack(file));
                }
            }
            return (added, seen);
        });

        // drop tracks from scanned folders whose files are gone
        _d.Tracks.RemoveAll(t => roots.Any(r => t.FilePath.StartsWith(r, StringComparison.OrdinalIgnoreCase))
                                 && !result.seen.Contains(t.FilePath) && !File.Exists(t.FilePath));
        _d.Tracks.AddRange(result.added);
        Index();
        Save();
        Changed?.Invoke();
        return result.added.Count;
    }

    public void RemoveFolder(string folder)
    {
        if (_d.Folders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)) > 0) { Save(); Changed?.Invoke(); }
    }

    public void Remove(IEnumerable<Track> tracks)
    {
        var set = new HashSet<Track>(tracks);
        _d.Tracks.RemoveAll(set.Contains);
        Index();
        SaveSoon();
        Changed?.Invoke();
    }

    // ---- playlists ----
    public void CreatePlaylist(string name)
    {
        name = Cmp.Norm(name);
        if (string.IsNullOrWhiteSpace(name) || _d.Playlists.ContainsKey(name)) return;
        _d.Playlists[name] = new List<string>();
        Save();
        Changed?.Invoke();
    }

    public void DeletePlaylist(string name)
    {
        if (_d.Playlists.Remove(name)) { Save(); Changed?.Invoke(); }
    }

    public void AddToPlaylist(string name, IEnumerable<Track> tracks)
    {
        if (!_d.Playlists.TryGetValue(name, out var list)) return;
        foreach (var t in tracks)
        {
            string p = Paths.ToStored(t.FilePath);
            if (!list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p);
        }
        Save();
        Changed?.Invoke();
    }

    public void RemoveFromPlaylist(string name, IEnumerable<Track> tracks)
    {
        if (!_d.Playlists.TryGetValue(name, out var list)) return;
        var remove = new HashSet<string>(tracks.Select(t => Paths.ToStored(t.FilePath)), StringComparer.OrdinalIgnoreCase);
        list.RemoveAll(remove.Contains);
        Save();
        Changed?.Invoke();
    }

    public List<Track> PlaylistTracks(string name) =>
        _d.Playlists.TryGetValue(name, out var list)
            ? list.Select(p => Find(Paths.FromStored(p))).Where(t => t != null).ToList()
            : new List<Track>();
}
