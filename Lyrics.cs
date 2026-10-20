using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PortablePlayer;

public sealed class LyricLine
{
    public double Time { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>Lyrics of one song: timed lines (LRC) or plain text.</summary>
public sealed class LyricsDoc
{
    public List<LyricLine> Lines { get; set; } = new();
    public string Plain { get; set; } = "";
    public bool Synced => Lines.Count > 0;
    public string Source { get; set; } = "";
    public bool Instrumental { get; set; }
    public bool IsEmpty => !Instrumental && Lines.Count == 0 && string.IsNullOrWhiteSpace(Plain);
}

/// <summary>
/// Finds lyrics. Order: a file the user imported into Vibe, a .lrc/.txt next to the song, lyrics embedded in the file's tag,
/// the online cache, and only then LRCLIB (free, open lyrics database, no key) when online info is switched on.
/// Nothing is written next to the user's music; online results are cached in data\cache\lyrics.
/// </summary>
public static class LyricsService
{
    public static string SavedDir => Directory.CreateDirectory(Path.Combine(Paths.DataDir, "lyrics")).FullName;
    static string CacheDir => Directory.CreateDirectory(Path.Combine(Paths.DataDir, "cache", "lyrics")).FullName;

    public static string KeyOf(Track t) => Remote.Hash((t.Artist ?? "").Trim().ToLowerInvariant() + "|" + (t.Title ?? "").Trim().ToLowerInvariant());
    public static string SavedFile(Track t) => Path.Combine(SavedDir, KeyOf(t) + ".lrc");

    // ------------------------------------------------------------------ parsing
    static readonly Regex Stamp = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
    static readonly Regex Meta = new(@"^\[(ar|ti|al|au|by|length|re|ve|la|#):.*\]\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex OffsetTag = new(@"^\[offset:\s*(-?\d+)\s*\]", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>Parses LRC (several time stamps per line allowed) or plain text.</summary>
    public static LyricsDoc Parse(string text, string source)
    {
        var doc = new LyricsDoc { Source = source };
        if (string.IsNullOrWhiteSpace(text)) return doc;
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('﻿');

        double offset = 0;
        var om = OffsetTag.Match(text);
        if (om.Success && double.TryParse(om.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out double ms)) offset = ms / 1000.0;

        var timed = new List<LyricLine>();
        var plain = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd();
            if (Meta.IsMatch(line) || OffsetTag.IsMatch(line)) continue;
            var ms2 = Stamp.Matches(line);
            if (ms2.Count == 0) { plain.AppendLine(line); continue; }
            string body = Stamp.Replace(line, "").Trim();
            plain.AppendLine(body);
            foreach (Match m in ms2)
            {
                double min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                double sec = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                double frac = 0;
                if (m.Groups[3].Success)
                {
                    string f = m.Groups[3].Value;
                    frac = int.Parse(f, CultureInfo.InvariantCulture) / Math.Pow(10, f.Length);
                }
                timed.Add(new LyricLine { Time = Math.Max(0, min * 60 + sec + frac - offset), Text = body });
            }
        }
        if (timed.Count > 0) doc.Lines = timed.OrderBy(l => l.Time).ToList();
        doc.Plain = plain.ToString().Trim();
        return doc;
    }

    // ------------------------------------------------------------------ lookup
    /// <summary>Local sources + cache only (fast, no network). Null when nothing is found.</summary>
    public static LyricsDoc FindLocal(Track t)
    {
        try
        {
            string saved = SavedFile(t);
            if (File.Exists(saved)) { var d = Parse(File.ReadAllText(saved), "Saved by you"); if (!d.IsEmpty) return d; }
        }
        catch { }

        if (!t.IsOnline && !string.IsNullOrEmpty(t.FilePath))
        {
            foreach (var ext in new[] { ".lrc", ".txt" })
            {
                try
                {
                    string side = Path.ChangeExtension(t.FilePath, ext);
                    if (File.Exists(side)) { var d = Parse(File.ReadAllText(side), "File next to the song (" + ext + ")"); if (!d.IsEmpty) return d; }
                }
                catch { }
            }
            try
            {
                if (File.Exists(t.FilePath))
                {
                    using var f = TagLib.File.Create(t.FilePath);
                    string emb = f.Tag.Lyrics;
                    if (!string.IsNullOrWhiteSpace(emb)) { var d = Parse(emb, "Embedded in the file"); if (!d.IsEmpty) return d; }
                }
            }
            catch { /* unreadable tag: just no lyrics from here */ }
        }

        try
        {
            string cache = Path.Combine(CacheDir, KeyOf(t) + ".json");
            if (File.Exists(cache))
            {
                using var j = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cache));
                var r = j.RootElement;
                var d = Parse(Remote.Str(r, "text"), Remote.Str(r, "source"));
                d.Instrumental = r.TryGetProperty("instrumental", out var ins) && ins.ValueKind == System.Text.Json.JsonValueKind.True;
                if (!d.IsEmpty || d.Instrumental) return d;
            }
        }
        catch { }
        return null;
    }

    /// <summary>LRCLIB lookup (exact match first, then a search). Caches a hit. Null when the song is not there.</summary>
    public static async Task<LyricsDoc> FindOnlineAsync(Track t, CancellationToken ct)
    {
        string artist = Remote.Q(t.Artist), title = Remote.Q(t.Title), album = Remote.Q(t.Album);
        int dur = (int)Math.Round(t.DurationSec);
        const string Base = "https://lrclib.net/api/";

        System.Text.Json.JsonElement? hit = null;
        System.Text.Json.JsonDocument keep1 = null, keep2 = null;
        try
        {
            try
            {
                string url = $"{Base}get?artist_name={artist}&track_name={title}" +
                             (string.IsNullOrWhiteSpace(t.Album) || t.Album == "Unknown Album" ? "" : $"&album_name={album}") +
                             (dur > 0 ? $"&duration={dur}" : "");
                keep1 = await Remote.GetJsonAsync(url, ct);
                hit = keep1.RootElement;
            }
            catch (RemoteException ex) when (ex.Message.Contains("404")) { hit = null; }

            if (hit == null)
            {
                keep2 = await Remote.GetJsonAsync($"{Base}search?track_name={title}&artist_name={artist}", ct);
                var arr = keep2.RootElement;
                if (arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    System.Text.Json.JsonElement? best = null; double bestDiff = double.MaxValue;
                    foreach (var e in arr.EnumerateArray())
                    {
                        bool has = !string.IsNullOrWhiteSpace(Remote.Str(e, "syncedLyrics")) || !string.IsNullOrWhiteSpace(Remote.Str(e, "plainLyrics"));
                        if (!has) continue;
                        double diff = dur > 0 ? Math.Abs(Remote.Dbl(e, "duration") - dur) : 0;
                        if (!string.IsNullOrWhiteSpace(Remote.Str(e, "syncedLyrics"))) diff -= 0.5;   // prefer timed lyrics on a tie
                        if (diff < bestDiff) { bestDiff = diff; best = e; }
                    }
                    if (best != null && (dur <= 0 || bestDiff <= 5)) hit = best;
                }
            }

            if (hit == null) { Remote.Trace("Lyrics", ("provider", "LRCLIB"), ("result", "not found"), ("song", t.Artist + " - " + t.Title)); return null; }
            var h = hit.Value;
            string synced = Remote.Str(h, "syncedLyrics"), plain = Remote.Str(h, "plainLyrics");
            bool inst = h.TryGetProperty("instrumental", out var iv) && iv.ValueKind == System.Text.Json.JsonValueKind.True;
            string text = !string.IsNullOrWhiteSpace(synced) ? synced : plain;
            var doc = Parse(text, "LRCLIB (online)");
            doc.Instrumental = inst && doc.IsEmpty;
            if (doc.IsEmpty && !doc.Instrumental) return null;
            Remote.Trace("Lyrics", ("provider", "LRCLIB"), ("result", doc.Synced ? "synced" : doc.Instrumental ? "instrumental" : "plain"), ("song", t.Artist + " - " + t.Title));
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(new { text, source = "LRCLIB (online, cached)", instrumental = doc.Instrumental });
                Paths.WriteAtomic(Path.Combine(CacheDir, KeyOf(t) + ".json"), json);
            }
            catch { }
            return doc;
        }
        finally { keep1?.Dispose(); keep2?.Dispose(); }
    }

    /// <summary>Copies a .lrc/.txt chosen by the user into Vibe's own lyrics folder (never into the music folder).</summary>
    public static LyricsDoc Import(Track t, string file)
    {
        string text = File.ReadAllText(file);
        var doc = Parse(text, "Saved by you");
        if (doc.IsEmpty) return null;
        File.WriteAllText(SavedFile(t), text);
        return doc;
    }

    public static void RemoveSaved(Track t)
    {
        try { File.Delete(SavedFile(t)); } catch { }
    }

    public static void ForgetCache(Track t)
    {
        try { File.Delete(Path.Combine(CacheDir, KeyOf(t) + ".json")); } catch { }
    }
}
