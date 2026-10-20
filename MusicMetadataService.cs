using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PortablePlayer;

// ---------------------------------------------------------------------------------------------- data
/// <summary>Structured facts + biography for one artist.</summary>
public sealed class ArtistInfo
{
    public string Name { get; set; } = "";
    public string Mbid { get; set; } = "";
    public string WikidataId { get; set; } = "";
    public string Type { get; set; } = "";
    public string Country { get; set; } = "";
    public int BeginYear { get; set; }
    public bool Ended { get; set; }
    public List<string> Tags { get; set; } = new();
    public string Bio { get; set; } = "";
    public string BioSource { get; set; } = "";
    public string WikiTitle { get; set; } = "";
    public string WikiImage { get; set; } = "";
    public long Listeners { get; set; }
    [JsonIgnore] public RemoteException Error { get; set; }
}

/// <summary>Everything the artist page shows. This is what gets cached on disk (only sections that loaded properly).</summary>
public sealed class ArtistProfile
{
    public int Version { get; set; } = 2;
    public string Query { get; set; } = "";
    public DateTime FetchedUtc { get; set; }
    public bool HadLastFm { get; set; }

    public ArtistInfo Info { get; set; } = new();
    public RemoteState InfoState { get; set; } = RemoteState.NotLoaded;
    public string InfoMessage { get; set; } = "";

    /// <summary>Up to five pictures for the rotating hero (cached separately by ArtistImageService, so not stored here).</summary>
    [JsonIgnore] public List<HeroImage> Images { get; set; } = new();
    [JsonIgnore] public RemoteState HeroState { get; set; } = RemoteState.NotLoaded;

    public Section<RemoteTrack> Top { get; set; } = new();
    public Section<RelatedArtist> Similar { get; set; } = new();
    public Section<RecommendedTrack> Recommended { get; set; } = new();
    public Section<ReleaseInfo> Releases { get; set; } = new();

    /// <summary>Song the recommendations were seeded with (not cached; empty = the artist's own recommendations).</summary>
    [JsonIgnore] public string Seed { get; set; }
}

// ---------------------------------------------------------------------------------------------- service
/// <summary>
/// Artist metadata + staged loading of the whole artist profile. The UI only talks to this class (and, through it, to
/// ArtistImageService / RecommendationService / AudioProviderService) - never to an API.
///   MusicBrainz: identification, type / country / years / tags, Wikipedia + Wikidata links (1 request / s)
///   Last.fm:     biography, top tracks (optional free key)         Wikipedia: biography fallback
///   Deezer:      keyless top tracks, releases, related artists, previews
///   iTunes / TheAudioDB / SoundCloud (own API keys): fallbacks when the sources above fail or know nothing
/// </summary>
public sealed class MusicMetadataService
{
    public static MusicMetadataService Instance { get; } = new();
    static readonly TimeSpan ProfileTtl = TimeSpan.FromDays(7);

    public bool Enabled => Remote.Enabled;
    public bool HasLastFm => Remote.HasLastFm;

    // ------------------------------------------------------------------ searchArtist / resolveArtistId (MusicBrainz)
    public async Task<(string Mbid, string Name)?> SearchArtistAsync(string name, CancellationToken ct)
    {
        string q = $"artist:\"{(name ?? "").Replace("\"", " ")}\"";
        using var doc = await Remote.GetJsonAsync($"https://musicbrainz.org/ws/2/artist/?query={Remote.Q(q)}&fmt=json&limit=6", ct, true);
        if (!doc.RootElement.TryGetProperty("artists", out var arr)) return null;
        (string, string)? best = null; long bestScore = -1;
        foreach (var a in arr.EnumerateArray())
        {
            string n = Remote.Str(a, "name"), sort = Remote.Str(a, "sort-name");
            if (!Cmp.SameArtist(n, name) && !Cmp.SameArtist(sort, name)) continue;      // never guess a different artist
            long score = Remote.Lng(a, "score");
            if (score > bestScore) { bestScore = score; best = (Remote.Str(a, "id"), n); }
        }
        return best;
    }

    public async Task<string> ResolveArtistIdAsync(string name, CancellationToken ct) => (await SearchArtistAsync(name, ct))?.Mbid;

    // ------------------------------------------------------------------ getArtistInfo
    public async Task<ArtistInfo> GetArtistInfoAsync(string name, CancellationToken ct)
    {
        var info = new ArtistInfo { Name = name };
        string wikiUrl = null;
        RemoteException error = null;

        try
        {
            var hit = await SearchArtistAsync(name, ct);
            if (hit != null)
            {
                info.Mbid = hit.Value.Mbid;
                info.Name = hit.Value.Name;
                using var doc = await Remote.GetJsonAsync($"https://musicbrainz.org/ws/2/artist/{info.Mbid}?inc=url-rels+tags&fmt=json", ct, true);
                var r = doc.RootElement;
                info.Type = Remote.Str(r, "type");
                info.Country = Remote.Str(r, "country");
                if (r.TryGetProperty("life-span", out var ls))
                {
                    string b = Remote.Str(ls, "begin");
                    if (b.Length >= 4 && int.TryParse(b.Substring(0, 4), out int y)) info.BeginYear = y;
                    info.Ended = ls.TryGetProperty("ended", out var en) && en.ValueKind == JsonValueKind.True;
                }
                if (r.TryGetProperty("tags", out var tags))
                    info.Tags = tags.EnumerateArray().OrderByDescending(t => Remote.Lng(t, "count")).Select(t => Remote.Str(t, "name"))
                        .Where(t => t.Length > 0).Take(5).ToList();
                if (r.TryGetProperty("relations", out var rels))
                    foreach (var rel in rels.EnumerateArray())
                    {
                        string type = Remote.Str(rel, "type");
                        string res = rel.TryGetProperty("url", out var u) ? Remote.Str(u, "resource") : "";
                        if (type == "wikipedia" && res.Contains("en.wikipedia.org/wiki/")) wikiUrl = res;
                        else if (type == "wikidata" && res.Contains("/wiki/Q")) info.WikidataId = res.Substring(res.LastIndexOf('/') + 1);
                    }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { error = Remote.Classify(ex); }

        if (Remote.HasLastFm)
        {
            try { await LoadLastFmBioAsync(info, name, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { error ??= Remote.Classify(ex); }
        }

        if (string.IsNullOrWhiteSpace(info.Bio))
        {
            try
            {
                string title = wikiUrl != null ? wikiUrl.Substring(wikiUrl.LastIndexOf('/') + 1) : null;
                if (title == null && info.WikidataId.Length > 0) title = await WikidataTitleAsync(info.WikidataId, ct);
                if (title != null) await LoadWikipediaAsync(info, title, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { error ??= Remote.Classify(ex); }
        }

        // more sources for what is still missing (each one optional and independent)
        if (string.IsNullOrWhiteSpace(info.Bio) || info.Tags.Count == 0 || info.BeginYear == 0)
        {
            try { await AudioDb.FillInfoAsync(info, name, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { error ??= Remote.Classify(ex); }
        }
        if (string.IsNullOrWhiteSpace(info.Bio) && SoundCloud.Configured)
        {
            try { await SoundCloud.FillInfoAsync(info, name, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { error ??= Remote.Classify(ex); }
        }

        bool anything = !string.IsNullOrWhiteSpace(info.Bio) || info.Mbid.Length > 0 || info.Tags.Count > 0;
        info.Error = anything ? null : error;
        return info;
    }

    static readonly Regex Html = new("<[^>]+>", RegexOptions.Compiled);

    static string CleanBio(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        int more = html.IndexOf("<a href", StringComparison.OrdinalIgnoreCase);        // Last.fm appends a "Read more" link
        if (more > 0) html = html.Substring(0, more);
        string t = WebUtility.HtmlDecode(Html.Replace(html, ""));
        t = Regex.Replace(t.Replace("\r", ""), @"\n{3,}", "\n\n").Trim();
        return t.Length > 4000 ? t.Substring(0, 4000).TrimEnd() + "…" : t;
    }

    static async Task LoadLastFmBioAsync(ArtistInfo info, string name, CancellationToken ct)
    {
        using var doc = await Remote.GetJsonAsync(Remote.LastFmUrl("artist.getinfo", $"artist={Remote.Q(name)}&autocorrect=1"), ct);
        var root = doc.RootElement;
        if (!Remote.LastFmOk(root) || !root.TryGetProperty("artist", out var a)) return;
        if (a.TryGetProperty("bio", out var bio))
        {
            string text = CleanBio(Remote.Str(bio, "content"));
            if (text.Length == 0) text = CleanBio(Remote.Str(bio, "summary"));
            if (text.Length > 0) { info.Bio = text; info.BioSource = "Last.fm"; }
        }
        if (a.TryGetProperty("stats", out var st)) info.Listeners = Remote.Lng(st, "listeners");
        if (info.Tags.Count == 0 && a.TryGetProperty("tags", out var tg) && tg.TryGetProperty("tag", out var tl))
            info.Tags = tl.EnumerateArray().Select(t => Remote.Str(t, "name")).Where(t => t.Length > 0).Take(5).ToList();
    }

    static async Task<string> WikidataTitleAsync(string qid, CancellationToken ct)
    {
        using var doc = await Remote.GetJsonAsync(
            $"https://www.wikidata.org/w/api.php?action=wbgetentities&ids={Remote.Q(qid)}&props=sitelinks&sitefilter=enwiki&format=json", ct);
        if (doc.RootElement.TryGetProperty("entities", out var ents) && ents.TryGetProperty(qid, out var e) &&
            e.TryGetProperty("sitelinks", out var sl) && sl.TryGetProperty("enwiki", out var w))
            return Uri.EscapeDataString(Remote.Str(w, "title").Replace(' ', '_'));
        return null;
    }

    static async Task LoadWikipediaAsync(ArtistInfo info, string title, CancellationToken ct)
    {
        using var doc = await Remote.GetJsonAsync($"https://en.wikipedia.org/api/rest_v1/page/summary/{title}", ct);
        var r = doc.RootElement;
        string extract = Remote.Str(r, "extract");
        if (extract.Length > 0) { info.Bio = extract.Trim(); info.BioSource = "Wikipedia"; info.WikiTitle = Remote.Str(r, "title"); }
        if (r.TryGetProperty("originalimage", out var oi)) info.WikiImage = Remote.Str(oi, "source");
        else if (r.TryGetProperty("thumbnail", out var th)) info.WikiImage = Remote.Str(th, "source");
    }

    // ------------------------------------------------------------------ getArtistTopTracks (remote - not limited to the library)
    public async Task<List<RemoteTrack>> GetArtistTopTracksAsync(string name, CancellationToken ct)
    {
        var list = new List<RemoteTrack>();
        RemoteException error = null;

        if (Remote.HasLastFm)
        {
            try
            {
                using var doc = await Remote.GetJsonAsync(Remote.LastFmUrl("artist.gettoptracks", $"artist={Remote.Q(name)}&autocorrect=1&limit=30"), ct);
                if (Remote.LastFmOk(doc.RootElement) && doc.RootElement.TryGetProperty("toptracks", out var tt) && tt.TryGetProperty("track", out var arr))
                    foreach (var t in arr.EnumerateArray())
                    {
                        string title = Remote.Str(t, "name");
                        if (title.Length > 0)
                            list.Add(new RemoteTrack { Title = title, Artist = name, Plays = Remote.Lng(t, "playcount"), Url = Remote.Str(t, "url"), Source = "Last.fm" });
                    }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { error = Remote.Classify(ex); }
        }

        try
        {
            var dz = await Remote.DeezerFindAsync(name, ct);
            if (dz != null)
            {
                var dtracks = new List<RemoteTrack>();
                using var doc = await Remote.GetJsonAsync($"https://api.deezer.com/artist/{dz.Id}/top?limit=30", ct);
                Remote.DeezerCheck(doc.RootElement);
                if (doc.RootElement.TryGetProperty("data", out var data))
                    foreach (var t in data.EnumerateArray())
                    {
                        var rt = Remote.DeezerTrack(t);
                        if (rt.Title.Length > 0) { if (rt.Artist.Length == 0) rt.Artist = name; dtracks.Add(rt); }
                    }
                if (list.Count == 0) list = dtracks;
                else
                    foreach (var l in list)                 // Last.fm ranking, enriched with Deezer covers / previews / links
                    {
                        var m = dtracks.FirstOrDefault(d => Cmp.TitleKey(d.Title) == Cmp.TitleKey(l.Title));
                        if (m == null) continue;
                        l.DeezerId = m.DeezerId; l.CoverUrl = m.CoverUrl; l.Album = m.Album; l.AlbumUrl = m.AlbumUrl;
                        l.DurationSec = m.DurationSec; l.HasPreview = m.HasPreview; l.PreviewUrl = m.PreviewUrl; l.PreviewChecked = true;
                        if (string.IsNullOrEmpty(l.Url)) l.Url = m.Url;
                    }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { error ??= Remote.Classify(ex); }

        if (list.Count == 0)                                  // Deezer unavailable or without this artist: iTunes (keyless), then SoundCloud
        {
            try { list = await Itunes.TopTracksAsync(name, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { error ??= Remote.Classify(ex); }
        }
        if (list.Count == 0 && SoundCloud.Configured)
        {
            try { list = await SoundCloud.TracksAsync(name, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { error ??= Remote.Classify(ex); }
        }

        if (list.Count > 0) return list;
        if (error != null) throw error;
        return list;
    }

    // ------------------------------------------------------------------ releases (Deezer, then iTunes)
    public async Task<List<ReleaseInfo>> GetArtistReleasesAsync(string name, CancellationToken ct)
    {
        RemoteException error = null;
        try
        {
            var list = await DeezerReleasesAsync(name, ct);
            if (list.Count > 0) return list;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { error = Remote.Classify(ex); }

        try { return await Itunes.AlbumsAsync(name, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { if (error != null) throw error; throw; }
    }

    async Task<List<ReleaseInfo>> DeezerReleasesAsync(string name, CancellationToken ct)
    {
        var list = new List<ReleaseInfo>();
        var dz = await Remote.DeezerFindAsync(name, ct);
        if (dz == null) return list;
        using var doc = await Remote.GetJsonAsync($"https://api.deezer.com/artist/{dz.Id}/albums?limit=60", ct);
        Remote.DeezerCheck(doc.RootElement);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return list;
        foreach (var a in data.EnumerateArray())
        {
            string title = Remote.Str(a, "title");
            if (title.Length == 0) continue;
            string cover = Remote.Str(a, "cover_big"); if (cover.Length == 0) cover = Remote.Str(a, "cover_medium");
            list.Add(new ReleaseInfo { Title = title, CoverUrl = cover, Url = Remote.Str(a, "link") is { Length: > 0 } l ? l : $"https://www.deezer.com/album/{Remote.Lng(a, "id")}",
                                       Date = Remote.Str(a, "release_date"), Type = Remote.Str(a, "record_type") });
        }
        return list.GroupBy(r => Cmp.Key(r.Title)).Select(g => g.First())
                   .OrderByDescending(r => r.Date).Take(8).ToList();
    }

    // ------------------------------------------------------------------ getArtistImage (small square portrait for cards)
    public Task<string> GetArtistImageAsync(string name, CancellationToken ct) => ArtistImageService.Instance.GetBestArtistImageAsync(name, ct);

    // ------------------------------------------------------------------ cache
    static string ProfileFile(string name) => Path.Combine(Remote.ArtistDir, Remote.Hash(Cmp.ArtistKey(name)) + ".json");

    ArtistProfile ReadCache(string name)
    {
        string f = ProfileFile(name);
        try
        {
            if (!File.Exists(f)) return null;
            var p = JsonSerializer.Deserialize<ArtistProfile>(File.ReadAllText(f), Paths.Json);
            if (p == null || p.Version != 2 || p.Info == null || p.Top == null || p.Similar == null || p.Recommended == null || p.Releases == null)
                throw new JsonException("outdated or damaged cache");
            return p;
        }
        catch { try { File.Delete(f); } catch { } return null; }         // a bad cache file must never break the page
    }

    static bool Needs<T>(Section<T> s) => s.State is RemoteState.NotLoaded or RemoteState.Loading or RemoteState.Failed or RemoteState.Offline or RemoteState.Disabled;

    static void Clean<T>(Section<T> s)
    {
        if (s.State is RemoteState.Loading or RemoteState.Failed or RemoteState.Offline or RemoteState.Disabled)
        { s.State = RemoteState.NotLoaded; s.Items = new(); s.Message = ""; }
    }

    static Section<T> Snap<T>(Section<T> s) => new() { State = s.State, Message = s.Message, Items = s.Items };

    void WriteCache(ArtistProfile p, Section<RecommendedTrack> recommendedToStore)
    {
        try
        {
            var copy = new ArtistProfile
            {
                Query = p.Query, FetchedUtc = DateTime.UtcNow, HadLastFm = Remote.HasLastFm, Info = p.Info,
                InfoState = p.InfoState is RemoteState.Ok or RemoteState.NoData ? p.InfoState : RemoteState.NotLoaded,
                Top = Snap(p.Top), Similar = Snap(p.Similar), Recommended = Snap(recommendedToStore), Releases = Snap(p.Releases)
            };
            Clean(copy.Top); Clean(copy.Similar); Clean(copy.Recommended); Clean(copy.Releases);
            Paths.WriteAtomic(ProfileFile(p.Query), JsonSerializer.Serialize(copy, Paths.Json));
        }
        catch (Exception ex) { Paths.Log(ex); }
    }

    // ------------------------------------------------------------------ the staged profile
    /// <summary>
    /// Loads an artist profile in stages and calls <paramref name="update"/> after each one (UI thread), so the page is usable
    /// long before everything is in. Cached sections are reused; failed ones are retried. Three light chains run side by side:
    ///   picture: artist cutout        facts: biography / MusicBrainz        music: top tracks → similar artists → releases → recommendations
    /// Recommendations (metadata) never wait for, or depend on, any audio provider.
    /// </summary>
    public async Task<ArtistProfile> GetProfileAsync(string name, string seedTrack, Action<ArtistProfile> update, CancellationToken ct, bool refresh = false)
    {
        var cached = ReadCache(name);
        bool expired = cached == null || DateTime.UtcNow - cached.FetchedUtc > ProfileTtl || (Remote.HasLastFm && !cached.HadLastFm);
        var p = cached != null && !expired && !refresh ? cached : new ArtistProfile { Query = name, Info = new ArtistInfo { Name = name } };
        var storedRecs = p.Recommended;
        p.Query = name; p.Seed = string.IsNullOrWhiteSpace(seedTrack) ? null : seedTrack;
        Remote.Trace("ArtistProfile", ("artist", name), ("seed", p.Seed ?? ""), ("cache", cached == null ? "miss" : expired ? "expired" : refresh ? "refresh" : "hit"));

        if (!Remote.Enabled)
        {
            p.Top.State = p.Similar.State = p.Recommended.State = p.Releases.State = RemoteState.Disabled;
            p.InfoState = RemoteState.Disabled;
            // the artist pictures are a local folder: they do not need online discovery
            try { p.Images = await ArtistImageService.Instance.GetArtistImagesAsync(name, null, null, ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { return p; } catch { p.Images = new(); }
            p.HeroState = RemoteState.Ok;
            update?.Invoke(p);
            return p;
        }

        bool needHero = p.HeroState != RemoteState.Ok, needInfo = p.InfoState is not (RemoteState.Ok or RemoteState.NoData);
        bool needTop = Needs(p.Top), needSim = Needs(p.Similar), needRel = Needs(p.Releases);
        bool needRec = p.Seed != null || Needs(p.Recommended);
        if (!(needHero || needInfo || needTop || needSim || needRel || needRec)) { update?.Invoke(p); return p; }

        if (needTop) p.Top.State = RemoteState.Loading;
        if (needSim) p.Similar.State = RemoteState.Loading;
        if (needRel) p.Releases.State = RemoteState.Loading;
        if (needRec) p.Recommended.State = RemoteState.Loading;
        if (needInfo) p.InfoState = RemoteState.Loading;
        if (needHero) p.HeroState = RemoteState.Loading;
        update?.Invoke(p);

        void Push() { if (!ct.IsCancellationRequested) update?.Invoke(p); }

        // old results of the same kind (even expired ones) are better than an error message
        var old = cached;

        async Task Stage<T>(Section<T> sec, Section<T> oldSection, string noun, Func<Task<List<T>>> work)
        {
            try
            {
                var items = await work();
                sec.Items = items; sec.Message = "";
                sec.State = items.Count > 0 ? RemoteState.Ok : RemoteState.NoData;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                var re = Remote.Classify(ex);
                Remote.Trace("ArtistProfile", ("artist", name), ("section", noun), ("failed", re.Kind), ("provider", re.Provider), ("detail", re.Message));
                if (oldSection is { State: RemoteState.Ok } && oldSection.Items.Count > 0)
                { sec.Items = oldSection.Items; sec.State = RemoteState.Ok; sec.Message = "Showing saved results. " + re.Describe(noun); }
                else { sec.State = re.State; sec.Message = re.Describe(noun); sec.Items = new(); }
            }
            Push();
        }

        Task factsTask = Task.CompletedTask;                 // assigned below; Picture() may wait for it

        async Task Picture()
        {
            // Local folder first; only when it has nothing for this artist the free picture APIs are asked (each on its own, never blocking the rest).
            if (!needHero) return;
            // nothing local for this artist: wait briefly for the biography stage, its Wikipedia/Wikidata ids give the APIs more to work with
            if (ArtistImageService.FindFiles(name).Count == 0)
                try { await factsTask; } catch { }
            try { p.Images = await ArtistImageService.Instance.GetArtistImagesAsync(name, p.Info, null, ct, remote: true); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Remote.Trace("ArtistProfile", ("artist", name), ("section", "pictures"), ("failed", ex.GetType().Name), ("detail", ex.Message));
                p.Images = new List<HeroImage>();
            }
            p.HeroState = RemoteState.Ok;
            Push();
        }

        async Task Facts()
        {
            if (!needInfo) return;
            try
            {
                var info = await GetArtistInfoAsync(name, ct);
                if (info.Error != null) { p.InfoState = info.Error.State; p.InfoMessage = info.Error.Describe("biography details"); }
                else
                {
                    p.Info = info;
                    p.InfoState = !string.IsNullOrWhiteSpace(info.Bio) || info.Mbid.Length > 0 || info.Tags.Count > 0 ? RemoteState.Ok : RemoteState.NoData;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { var re = Remote.Classify(ex); p.InfoState = re.State; p.InfoMessage = re.Describe("biography details"); }
            Push();
        }

        factsTask = Facts();

        async Task Music()
        {
            if (needTop) await Stage(p.Top, old?.Top, "popular tracks", () => GetArtistTopTracksAsync(name, ct));
            if (ct.IsCancellationRequested) return;
            await factsTask;                                  // the MusicBrainz id makes the similar-artist lookup more accurate
            if (needSim)
                await Stage(p.Similar, old?.Similar, "related artists", () => RecommendationService.Instance.GetArtistRecommendationsAsync(name, p.Info.Mbid, ct));
            if (ct.IsCancellationRequested) return;
            if (needRel) await Stage(p.Releases, old?.Releases, "releases", () => GetArtistReleasesAsync(name, ct));
            if (ct.IsCancellationRequested) return;
            if (needRec)
            {
                try
                {
                    var r = await RecommendationService.Instance.GetTrackRecommendationsAsync(name, p.Seed, p.Top.Items, ct);
                    p.Recommended.Items = r.Items;
                    p.Recommended.Message = r.Note;
                    p.Recommended.State = r.Items.Count > 0 ? RemoteState.Ok : RemoteState.NoData;
                    Remote.Trace("ArtistProfile", ("artist", name), ("section", "recommendations"), ("results", r.Items.Count), ("cache", r.FromCache), ("note", r.Note));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    var re = Remote.Classify(ex);
                    Remote.Trace("ArtistProfile", ("artist", name), ("section", "recommendations"), ("failed", re.Kind), ("provider", re.Provider), ("detail", re.Message));
                    p.Recommended.State = re.State; p.Recommended.Message = re.Describe("recommendations"); p.Recommended.Items = new();
                }
                Push();
            }
        }

        await Task.WhenAll(Picture(), factsTask, Music());

        if (ct.IsCancellationRequested) return p;
        bool anyOk = p.Top.State == RemoteState.Ok || p.Similar.State == RemoteState.Ok || p.InfoState == RemoteState.Ok;
        if (anyOk) WriteCache(p, p.Seed != null ? (storedRecs.State == RemoteState.Ok ? storedRecs : new Section<RecommendedTrack>()) : p.Recommended);
        update?.Invoke(p);
        return p;
    }

    // ------------------------------------------------------------------ cache management (Settings → Storage)
    public (long ProfileBytes, long ImageBytes) CacheSize()
    {
        static long Size(string dir) { try { return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles().Sum(f => f.Length) : 0; } catch { return 0; } }
        string map = Path.Combine(Paths.DataDir, "cache", "artist-images.json");
        return (Size(Path.Combine(Paths.DataDir, "cache", "artists")) + (File.Exists(map) ? new FileInfo(map).Length : 0),
                Size(Path.Combine(Paths.DataDir, "cache", "images")));
    }

    public void ClearProfiles()
    {
        try { foreach (var f in Directory.EnumerateFiles(Remote.ArtistDir)) File.Delete(f); } catch { }
        ArtistImageService.Instance.ClearMap();
        RecommendationService.ClearCache();
    }

    public void ClearImages()
    {
        try { foreach (var f in Directory.EnumerateFiles(Remote.ImageDir)) File.Delete(f); } catch { }
        ArtistImageService.Instance.ClearSets();
    }
}
