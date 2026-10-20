using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PortablePlayer;

/// <summary>A recommended song. Pure metadata: it exists whether or not the song can be played anywhere.</summary>
public sealed class RecommendedTrack
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string ArtistMbid { get; set; } = "";
    public string TrackMbid { get; set; } = "";
    public double SimilarityScore { get; set; }
    public string ArtworkUrl { get; set; } = "";
    public string Source { get; set; } = "";               // lastfm | listenbrainz | deezer | library
    public string ExternalUrl { get; set; } = "";
    public long DeezerId { get; set; }
    /// <summary>Filled when merged with the library (true = you own this song).</summary>
    public bool LocalMatch { get; set; }
    /// <summary>Full-length playback exists. No free provider offers it, so this is false for every online song.</summary>
    public bool Playable { get; set; }
    public bool PreviewAvailable { get; set; }
    [JsonIgnore] public string PreviewUrl { get; set; }

    public RemoteTrack ToRemote() => new()
    {
        Title = Title, Artist = Artist, Album = Album, Url = ExternalUrl, CoverUrl = ArtworkUrl, Source = Source,
        DeezerId = DeezerId, HasPreview = PreviewAvailable, PreviewUrl = PreviewUrl, Score = SimilarityScore,
        PreviewChecked = Source == "deezer"                    // Deezer data tells us for sure; others are checked later
    };
}

public sealed class RecommendationResult
{
    public List<RecommendedTrack> Items { get; set; } = new();
    /// <summary>Shown under the list: "saved recommendations", "from your library", ...</summary>
    public string Note { get; set; } = "";
    public bool FromCache { get; set; }
}

/// <summary>
/// Similar artists and similar tracks. Independent from AudioProviderService on purpose: a recommendation is metadata, and the
/// absence of any audio source never turns into a recommendation error. Pipeline for a song:
///   normalize artist + title → resolve MBIDs → Last.fm track.getSimilar (mbid, then names, then autocorrected / normalized)
///   → MusicBrainz + ListenBrainz → Deezer artist radio (secondary) → saved results → library-based suggestions.
/// Every step is written to data\discovery.log.
/// </summary>
public sealed class RecommendationService
{
    public static RecommendationService Instance { get; } = new();
    static readonly TimeSpan CacheTtl = TimeSpan.FromDays(3);

    static string Normalized(string name)
    {
        string n = Regex.Replace(name ?? "", @"\s*[\(\[].*?[\)\]]", " ");
        n = Regex.Replace(n, @"(?i)^the\s+", "");
        n = Regex.Replace(n, @"(?i)\s+(feat\.?|ft\.?|featuring)\s+.*$", "");
        return Cmp.Norm(n);
    }

    static string TitleBase(string title)
    {
        string t = Regex.Replace(title ?? "", @"\s*[\(\[].*?[\)\]]", " ");
        t = Regex.Replace(t, @"(?i)\s+-\s+(remaster(ed)?|\d{4}.*|live.*|mono|stereo|single version|radio edit).*$", "");
        t = Regex.Replace(t, @"(?i)\s+(feat\.?|ft\.?|featuring)\s+.*$", "");
        return Cmp.Norm(t);
    }

    static void Log(string what, string artist, string track, string provider, string method, string mbid, params (string, object)[] more)
    {
        var f = new List<(string, object)> { ("what", what), ("artist", artist ?? ""), ("track", track ?? ""), ("provider", provider), ("method", method), ("mbid", mbid ?? "") };
        f.AddRange(more);
        Remote.Trace("RecommendationService", f.ToArray());
    }

    // =================================================================================== similar artists
    /// <summary>
    /// Last.fm by MusicBrainz id, Last.fm by name (autocorrect), Last.fm by a normalized name, ListenBrainz by id, Deezer related.
    /// Empty = every lookup worked and found nothing. A RemoteException = the lookups failed.
    /// </summary>
    public async Task<List<RelatedArtist>> GetSimilarArtistsAsync(string name, string mbid, CancellationToken ct)
    {
        var found = new List<RelatedArtist>();
        RemoteException lastError = null;
        int succeeded = 0;

        async Task Try(string provider, string method, Func<Task<List<RelatedArtist>>> strategy)
        {
            if (found.Count >= 8) return;
            try
            {
                var r = await strategy();
                succeeded++;
                Log("similar-artists", name, null, provider, method, mbid, ("results", r.Count));
                foreach (var a in r)
                {
                    if (string.IsNullOrWhiteSpace(a.Name) || Cmp.SameArtist(a.Name, name)) continue;
                    if (found.Any(f => Cmp.SameArtist(f.Name, a.Name))) continue;
                    found.Add(a);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                lastError = Remote.Classify(ex);
                Log("similar-artists", name, null, provider, method, mbid, ("error", lastError.Kind), ("detail", lastError.Message));
            }
        }

        if (Remote.HasLastFm)
        {
            if (!string.IsNullOrEmpty(mbid)) await Try("Last.fm", "artist.getsimilar(mbid)", () => LastFmSimilarArtists($"mbid={Remote.Q(mbid)}", ct));
            await Try("Last.fm", "artist.getsimilar(name,autocorrect)", () => LastFmSimilarArtists($"artist={Remote.Q(name)}", ct));
            string norm = Normalized(name);
            if (!Cmp.Eq(norm, name)) await Try("Last.fm", "artist.getsimilar(normalized)", () => LastFmSimilarArtists($"artist={Remote.Q(norm)}", ct));
        }
        if (!string.IsNullOrEmpty(mbid)) await Try("ListenBrainz", "similar-artists", () => ListenBrainzSimilarArtists(mbid, ct));
        await Try("Deezer", "artist/related", () => DeezerRelated(name, ct));

        if (found.Count > 0) return found.OrderByDescending(a => a.Match).Take(18).ToList();
        if (succeeded == 0 && lastError != null) throw lastError;
        return found;
    }

    static async Task<List<RelatedArtist>> LastFmSimilarArtists(string args, CancellationToken ct)
    {
        var list = new List<RelatedArtist>();
        using var doc = await Remote.GetJsonAsync(Remote.LastFmUrl("artist.getsimilar", args + "&autocorrect=1&limit=24"), ct);
        if (!Remote.LastFmOk(doc.RootElement)) return list;
        if (doc.RootElement.TryGetProperty("similarartists", out var sa) && sa.TryGetProperty("artist", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var a in arr.EnumerateArray())
                list.Add(new RelatedArtist { Name = Remote.Str(a, "name"), Match = Remote.Dbl(a, "match"), Source = "Last.fm" });
        return list;
    }

    static async Task<List<RelatedArtist>> ListenBrainzSimilarArtists(string mbid, CancellationToken ct)
    {
        var list = new List<RelatedArtist>();
        string url = "https://labs.api.listenbrainz.org/similar-artists/json?artist_mbids=" + Remote.Q(mbid) +
                     "&algorithm=session_based_days_9000_session_300_contribution_5_threshold_15_limit_50_skip_30";
        using var doc = await Remote.GetJsonAsync(url, ct);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d)) root = d;
        if (root.ValueKind != JsonValueKind.Array) return list;
        double max = 0;
        foreach (var a in root.EnumerateArray()) max = Math.Max(max, Remote.Dbl(a, "score"));
        foreach (var a in root.EnumerateArray())
        {
            string n = Remote.Str(a, "name");
            if (n.Length == 0) continue;
            list.Add(new RelatedArtist { Name = n, Match = max > 0 ? Math.Round(Remote.Dbl(a, "score") / max, 3) * 0.9 : 0, Source = "ListenBrainz" });
            if (list.Count >= 20) break;
        }
        return list;
    }

    static async Task<List<RelatedArtist>> DeezerRelated(string name, CancellationToken ct)
    {
        var list = new List<RelatedArtist>();
        var dz = await Remote.DeezerFindAsync(name, ct) ?? await Remote.DeezerFindAsync(Normalized(name), ct);
        if (dz == null) return list;
        using var doc = await Remote.GetJsonAsync($"https://api.deezer.com/artist/{dz.Id}/related?limit=20", ct);
        Remote.DeezerCheck(doc.RootElement);
        if (doc.RootElement.TryGetProperty("data", out var data))
            foreach (var a in data.EnumerateArray())
                list.Add(new RelatedArtist { Name = Remote.Str(a, "name"), ImageUrl = Remote.BestPicture(a), Source = "Deezer" });
        return list;
    }

    // =================================================================================== similar tracks
    sealed record TrackIds(string Artist, string Title, string TrackMbid, string ArtistMbid);

    /// <summary>Recommendations for a song (artist + title). With useCache = false the saved results are ignored (diagnostics).</summary>
    public async Task<RecommendationResult> GetSimilarTracksAsync(string artist, string track, CancellationToken ct, bool useCache = true)
    {
        var result = new RecommendationResult();
        string key = Remote.Hash(Cmp.ArtistKey(artist) + "|" + Cmp.TitleKey(track));
        Log("similar-tracks", artist, track, "-", "start", "", ("lastfm-key", Remote.HasLastFm));

        if (useCache)
        {
            var fresh = ReadCache(key, CacheTtl);
            if (fresh != null && fresh.Count > 0)
            {
                Log("similar-tracks", artist, track, "cache", "read", "", ("cache", "hit"), ("results", fresh.Count));
                result.Items = fresh; result.FromCache = true;
                return result;
            }
            Log("similar-tracks", artist, track, "cache", "read", "", ("cache", "miss"));
        }

        var found = new List<RecommendedTrack>();
        RemoteException lastError = null;
        int succeeded = 0;
        string cArtist = artist, cTrack = track, trackMbid = null, artistMbid = null;
        var triedMbids = new HashSet<string>();

        async Task Try(string provider, string method, Func<Task<List<RecommendedTrack>>> strategy)
        {
            try
            {
                var r = await strategy();
                succeeded++;
                Log("similar-tracks", artist, track, provider, method, trackMbid, ("results", r.Count));
                foreach (var t in r)
                {
                    if (string.IsNullOrWhiteSpace(t.Title)) continue;
                    if (Cmp.SameArtist(t.Artist, artist) && !string.IsNullOrEmpty(track) && Cmp.TitleKey(t.Title) == Cmp.TitleKey(track)) continue;   // not the seed itself
                    if (found.Any(f => Cmp.SameArtist(f.Artist, t.Artist) && Cmp.TitleKey(f.Title) == Cmp.TitleKey(t.Title))) continue;
                    found.Add(t);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                lastError = Remote.Classify(ex);
                Log("similar-tracks", artist, track, provider, method, trackMbid, ("error", lastError.Kind), ("detail", lastError.Message));
            }
        }

        bool haveSeed = !string.IsNullOrWhiteSpace(track);

        // 1. Last.fm: resolve the song (autocorrected names + ids), then ask for similar tracks
        if (Remote.HasLastFm && haveSeed)
        {
            try
            {
                var info = await LastFmTrackInfo(artist, track, ct);
                succeeded++;
                Log("similar-tracks", artist, track, "Last.fm", "track.getInfo", info?.TrackMbid, ("resolved", info != null), ("corrected", info == null ? "" : info.Artist + " - " + info.Title));
                if (info != null) { cArtist = info.Artist; cTrack = info.Title; trackMbid = info.TrackMbid; artistMbid = info.ArtistMbid; }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                lastError = Remote.Classify(ex);
                Log("similar-tracks", artist, track, "Last.fm", "track.getInfo", "", ("error", lastError.Kind), ("detail", lastError.Message));
            }

            if (!string.IsNullOrEmpty(trackMbid) && triedMbids.Add(trackMbid))
                await Try("Last.fm", "track.getSimilar(mbid)", () => LastFmSimilarTracks($"mbid={Remote.Q(trackMbid)}", ct));
            if (found.Count < 4) await Try("Last.fm", "track.getSimilar(artist+track)", () => LastFmSimilarTracks($"artist={Remote.Q(artist)}&track={Remote.Q(track)}", ct));
            if (found.Count < 4 && (!Cmp.Eq(cArtist, artist) || !Cmp.Eq(cTrack, track)))
                await Try("Last.fm", "track.getSimilar(autocorrected)", () => LastFmSimilarTracks($"artist={Remote.Q(cArtist)}&track={Remote.Q(cTrack)}", ct));
            string nArtist = Normalized(artist), nTrack = TitleBase(track);
            if (found.Count < 4 && (!Cmp.Eq(nArtist, artist) || !Cmp.Eq(nTrack, track)))
                await Try("Last.fm", "track.getSimilar(normalized)", () => LastFmSimilarTracks($"artist={Remote.Q(nArtist)}&track={Remote.Q(nTrack)}", ct));
        }

        // 2. MusicBrainz resolution → ids for Last.fm / ListenBrainz (keyless)
        if (found.Count < 4 && haveSeed)
        {
            try
            {
                var rec = await ResolveRecordingAsync(artist, track, ct);
                succeeded++;
                Log("similar-tracks", artist, track, "MusicBrainz", "recording-search", rec?.TrackMbid, ("resolved", rec != null));
                if (rec != null) { trackMbid ??= rec.TrackMbid; artistMbid ??= rec.ArtistMbid; }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                lastError = Remote.Classify(ex);
                Log("similar-tracks", artist, track, "MusicBrainz", "recording-search", "", ("error", lastError.Kind), ("detail", lastError.Message));
            }
            if (Remote.HasLastFm && !string.IsNullOrEmpty(trackMbid) && triedMbids.Add(trackMbid))
                await Try("Last.fm", "track.getSimilar(mbid-from-MusicBrainz)", () => LastFmSimilarTracks($"mbid={Remote.Q(trackMbid)}", ct));
            if (found.Count < 4 && !string.IsNullOrEmpty(trackMbid))
                await Try("ListenBrainz", "similar-recordings", () => ListenBrainzSimilarRecordings(trackMbid, ct));
        }

        // 3. Deezer artist radio: a secondary metadata provider (songs of similar artists)
        if (found.Count < 4) await Try("Deezer", "artist/radio", () => DeezerRadio(artist, ct));

        if (found.Count > 0)
        {
            result.Items = found.OrderByDescending(t => t.SimilarityScore).Take(14).ToList();
            WriteCache(key, result.Items);
            return result;
        }

        // 4. saved results (even old ones), 5. suggestions from the library
        var stale = ReadCache(key, TimeSpan.MaxValue);
        if (stale != null && stale.Count > 0)
        {
            Log("similar-tracks", artist, track, "cache", "stale", "", ("cache", "stale-hit"), ("results", stale.Count));
            result.Items = stale; result.FromCache = true;
            result.Note = lastError != null ? "Showing saved recommendations. " + lastError.Describe("recommendations") : "Showing saved recommendations.";
            return result;
        }

        var local = LocalFallback(artist, track);
        if (local.Count > 0)
        {
            Log("similar-tracks", artist, track, "library", "local-engine", "", ("results", local.Count));
            result.Items = local;
            result.Note = succeeded == 0 && lastError != null
                ? lastError.Describe("recommendations") + " These songs are from your library."
                : "No online matches yet. These songs from your library have a similar style.";
            return result;
        }

        if (succeeded == 0 && lastError != null) throw lastError;      // a real failure is reported as a failure
        Log("similar-tracks", artist, track, "-", "done", "", ("results", 0));
        return result;                                                  // every lookup worked: simply nothing found
    }

    // ---- Last.fm
    static async Task<TrackIds> LastFmTrackInfo(string artist, string track, CancellationToken ct)
    {
        using var doc = await Remote.GetJsonAsync(Remote.LastFmUrl("track.getInfo", $"artist={Remote.Q(artist)}&track={Remote.Q(track)}&autocorrect=1"), ct);
        if (!Remote.LastFmOk(doc.RootElement) || !doc.RootElement.TryGetProperty("track", out var t)) return null;
        string a = t.TryGetProperty("artist", out var ar) ? Remote.Str(ar, "name") : artist;
        string am = t.TryGetProperty("artist", out var ar2) ? Remote.Str(ar2, "mbid") : "";
        string name = Remote.Str(t, "name");
        return new TrackIds(a.Length > 0 ? a : artist, name.Length > 0 ? name : track, Remote.Str(t, "mbid"), am);
    }

    static async Task<List<RecommendedTrack>> LastFmSimilarTracks(string args, CancellationToken ct)
    {
        var list = new List<RecommendedTrack>();
        using var doc = await Remote.GetJsonAsync(Remote.LastFmUrl("track.getSimilar", args + "&autocorrect=1&limit=30"), ct);
        if (!Remote.LastFmOk(doc.RootElement)) return list;
        if (doc.RootElement.TryGetProperty("similartracks", out var st) && st.TryGetProperty("track", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var t in arr.EnumerateArray())
            {
                var a = t.TryGetProperty("artist", out var ar) ? ar : default;
                list.Add(new RecommendedTrack
                {
                    Title = Remote.Str(t, "name"), Artist = Remote.Str(a, "name"), ArtistMbid = Remote.Str(a, "mbid"),
                    TrackMbid = Remote.Str(t, "mbid"), SimilarityScore = Remote.Dbl(t, "match"),
                    ExternalUrl = Remote.Str(t, "url"), Source = "lastfm"                 // a web page, never an audio source
                });
            }
        return list;
    }

    // ---- MusicBrainz + ListenBrainz (keyless)
    static async Task<TrackIds> ResolveRecordingAsync(string artist, string title, CancellationToken ct)
    {
        string q = $"recording:\"{TitleBase(title).Replace("\"", " ")}\" AND artist:\"{Normalized(artist).Replace("\"", " ")}\"";
        using var doc = await Remote.GetJsonAsync($"https://musicbrainz.org/ws/2/recording/?query={Remote.Q(q)}&fmt=json&limit=6", ct, true);
        if (!doc.RootElement.TryGetProperty("recordings", out var arr)) return null;
        foreach (var r in arr.EnumerateArray())
        {
            if (Remote.Lng(r, "score") < 85) continue;
            if (Cmp.TitleKey(Remote.Str(r, "title")) != Cmp.TitleKey(title) && Cmp.TitleKey(Remote.Str(r, "title")) != Cmp.TitleKey(TitleBase(title))) continue;
            if (!r.TryGetProperty("artist-credit", out var credits)) continue;
            foreach (var c in credits.EnumerateArray())
            {
                var a = c.TryGetProperty("artist", out var ar) ? ar : default;
                if (Cmp.SameArtist(Remote.Str(a, "name"), artist) || Cmp.SameArtist(Remote.Str(c, "name"), artist))
                    return new TrackIds(Remote.Str(a, "name"), Remote.Str(r, "title"), Remote.Str(r, "id"), Remote.Str(a, "id"));
            }
        }
        return null;
    }

    static async Task<List<RecommendedTrack>> ListenBrainzSimilarRecordings(string mbid, CancellationToken ct)
    {
        var list = new List<RecommendedTrack>();
        foreach (string algo in new[] { "session_based_days_9000_session_300_contribution_5_threshold_15_limit_50_skip_30", "session_based_days_180_session_300_contribution_5_threshold_15_limit_50_skip_30" })
        {
            using var doc = await Remote.GetJsonAsync($"https://labs.api.listenbrainz.org/similar-recordings/json?recording_mbids={Remote.Q(mbid)}&algorithm={algo}", ct);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d)) root = d;
            if (root.ValueKind != JsonValueKind.Array) continue;
            double max = 0;
            foreach (var r in root.EnumerateArray()) max = Math.Max(max, Remote.Dbl(r, "score"));
            foreach (var r in root.EnumerateArray())
            {
                string title = Remote.Str(r, "recording_name"), artist = Remote.Str(r, "artist_credit_name");
                if (title.Length == 0 || artist.Length == 0) continue;
                list.Add(new RecommendedTrack
                {
                    Title = title, Artist = artist, Album = Remote.Str(r, "release_name"), TrackMbid = Remote.Str(r, "recording_mbid"),
                    SimilarityScore = max > 0 ? Math.Round(Remote.Dbl(r, "score") / max, 3) * 0.9 : 0,
                    ExternalUrl = "https://musicbrainz.org/recording/" + Remote.Str(r, "recording_mbid"), Source = "listenbrainz"
                });
                if (list.Count >= 25) break;
            }
            if (list.Count > 0) break;
        }
        return list;
    }

    // ---- Deezer (secondary)
    static async Task<List<RecommendedTrack>> DeezerRadio(string artist, CancellationToken ct)
    {
        var list = new List<RecommendedTrack>();
        var dz = await Remote.DeezerFindAsync(artist, ct) ?? await Remote.DeezerFindAsync(Normalized(artist), ct);
        if (dz == null) return list;
        using var doc = await Remote.GetJsonAsync($"https://api.deezer.com/artist/{dz.Id}/radio?limit=30", ct);
        Remote.DeezerCheck(doc.RootElement);
        if (doc.RootElement.TryGetProperty("data", out var data))
            foreach (var t in data.EnumerateArray())
            {
                var rt = Remote.DeezerTrack(t);
                if (Cmp.SameArtist(rt.Artist, artist)) continue;                         // songs of OTHER artists only
                list.Add(new RecommendedTrack
                {
                    Title = rt.Title, Artist = rt.Artist, Album = rt.Album, ArtworkUrl = rt.CoverUrl, ExternalUrl = rt.Url,
                    DeezerId = rt.DeezerId, PreviewAvailable = rt.HasPreview, PreviewUrl = rt.PreviewUrl, Source = "deezer"
                });
            }
        return list;
    }

    // ---- local engine (last resort)
    static List<RecommendedTrack> LocalFallback(string artist, string track)
    {
        var lib = App.Instance.Library.Tracks;
        var seed = lib.FirstOrDefault(t => Cmp.SameArtist(t.Artist, artist) && Cmp.TitleKey(t.Title) == Cmp.TitleKey(track))
                   ?? lib.FirstOrDefault(t => Cmp.SameArtist(t.Artist, artist));
        string genre = seed?.Genre ?? "";
        int year = seed?.Year ?? 0;
        return lib.Where(t => !(Cmp.SameArtist(t.Artist, artist) && Cmp.TitleKey(t.Title) == Cmp.TitleKey(track)))
            .Select(t => (Track: t, Score:
                (genre.Length > 0 && Cmp.Eq(t.Genre, genre) ? 3.0 : 0) + (Cmp.SameArtist(t.Artist, artist) ? 1.5 : 0) +
                (year > 0 && t.Year > 0 && Math.Abs(t.Year - year) <= 5 ? 1.0 : 0) + (t.IsFavorite ? 0.3 : 0) + Math.Min(t.PlayCount, 10) * 0.05))
            .Where(x => x.Score >= 1.0)
            .OrderByDescending(x => x.Score).Take(10)
            .Select(x => new RecommendedTrack
            {
                Title = x.Track.Title, Artist = x.Track.Artist, Album = x.Track.Album, SimilarityScore = Math.Min(1, x.Score / 6),
                Source = "library", LocalMatch = true
            }).ToList();
    }

    // ---- saved results
    sealed class CacheFile { public DateTime FetchedUtc { get; set; } public List<RecommendedTrack> Items { get; set; } = new(); }
    static string CachePath(string key) => Path.Combine(Directory.CreateDirectory(Path.Combine(Paths.DataDir, "cache", "recs")).FullName, key + ".json");

    static List<RecommendedTrack> ReadCache(string key, TimeSpan maxAge)
    {
        try
        {
            string f = CachePath(key);
            if (!File.Exists(f)) return null;
            var c = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(f), Paths.Json);
            if (c?.Items == null) throw new JsonException("bad cache");
            return DateTime.UtcNow - c.FetchedUtc <= maxAge ? c.Items : null;
        }
        catch { try { File.Delete(CachePath(key)); } catch { } return null; }
    }

    static void WriteCache(string key, List<RecommendedTrack> items)
    {
        try { Paths.WriteAtomic(CachePath(key), JsonSerializer.Serialize(new CacheFile { FetchedUtc = DateTime.UtcNow, Items = items }, Paths.Json)); }
        catch (Exception ex) { Paths.Log(ex); }
    }

    public static void ClearCache() { try { foreach (var f in Directory.EnumerateFiles(Path.GetDirectoryName(CachePath("x")))) File.Delete(f); } catch { } }

    // =================================================================================== high level
    public Task<List<RelatedArtist>> GetArtistRecommendationsAsync(string name, string mbid, CancellationToken ct) => GetSimilarArtistsAsync(name, mbid, ct);

    /// <summary>Recommendations for a song, or - without one - for the artist (seeded by their top song, or the one you play most).</summary>
    public Task<RecommendationResult> GetTrackRecommendationsAsync(string artist, string seedTrack, IEnumerable<RemoteTrack> artistTop, CancellationToken ct)
    {
        string seed = !string.IsNullOrWhiteSpace(seedTrack) ? seedTrack
            : artistTop?.FirstOrDefault()?.Title
              ?? App.Instance.Library.Tracks.Where(t => Cmp.SameArtist(t.Artist, artist)).OrderByDescending(t => t.PlayCount).FirstOrDefault()?.Title;
        return GetSimilarTracksAsync(artist, seed, ct);
    }

    // =================================================================================== merge with the library
    public static Track FindLocal(LibraryStore lib, string artist, string title)
    {
        string key = Cmp.TitleKey(title);
        if (key.Length == 0) return null;
        return lib.Tracks.FirstOrDefault(t => Cmp.TitleKey(t.Title) == key &&
                                              (Cmp.SameArtist(t.Artist, artist) || Cmp.SameArtist(t.AlbumArtist, artist)));
    }

    // =================================================================================== diagnostics (Settings → Library)
    /// <summary>Runs the pipeline step by step for a known song and reports what each provider answered. No audio provider is involved.</summary>
    public async Task<string> DiagnoseAsync(string artist, string track, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        void Line(string s) => sb.AppendLine(s);
        async Task Step(string name, Func<Task<string>> f)
        {
            try { Line($"✓ {name}: {await f()}"); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { var re = Remote.Classify(ex); Line($"✗ {name}: {re.Kind}{(string.IsNullOrEmpty(re.Provider) ? "" : " (" + re.Provider + ")")} - {re.Message}"); }
        }

        Line($"Test song: {artist} - {track}");
        Line($"Network available: {System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()}   Last.fm key: {(Remote.HasLastFm ? "set" : "not set")}");
        string mbid = null;
        await Step("MusicBrainz artist", async () => { var r = await MusicMetadataService.Instance.SearchArtistAsync(artist, ct); return r == null ? "not found" : $"{r.Value.Name} ({r.Value.Mbid})"; });
        await Step("MusicBrainz recording", async () => { var r = await ResolveRecordingAsync(artist, track, ct); mbid = r?.TrackMbid; return r == null ? "not found" : $"{r.Title} ({r.TrackMbid})"; });
        if (Remote.HasLastFm)
        {
            await Step("Last.fm track.getInfo", async () => { var r = await LastFmTrackInfo(artist, track, ct); return r == null ? "not found" : $"{r.Artist} - {r.Title}, mbid {(string.IsNullOrEmpty(r.TrackMbid) ? "none" : r.TrackMbid)}"; });
            await Step("Last.fm track.getSimilar", async () => $"{(await LastFmSimilarTracks($"artist={Remote.Q(artist)}&track={Remote.Q(track)}", ct)).Count} songs");
        }
        else Line("– Last.fm: skipped (add a free API key in Settings → Library)");
        if (!string.IsNullOrEmpty(mbid)) await Step("ListenBrainz similar recordings", async () => $"{(await ListenBrainzSimilarRecordings(mbid, ct)).Count} songs");
        await Step("Deezer artist radio", async () => $"{(await DeezerRadio(artist, ct)).Count} songs");
        await Step("Full pipeline", async () =>
        {
            var r = await GetSimilarTracksAsync(artist, track, ct, useCache: false);
            int local = r.Items.Count(t => FindLocal(App.Instance.Library, t.Artist, t.Title) != null);
            return $"{r.Items.Count} recommendations ({local} LOCAL, {r.Items.Count - local} ONLINE){(r.Note.Length > 0 ? " - " + r.Note : "")}";
        });
        Line("The audio provider is not used by any of these steps.");
        Line($"Details: {Remote.LogFile}");
        return sb.ToString().TrimEnd();
    }
}
