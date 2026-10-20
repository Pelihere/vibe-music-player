using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PortablePlayer;

/// <summary>
/// Extra metadata sources, used only when the main ones (Deezer, MusicBrainz, ...) fail or have nothing.
/// iTunes Search needs no key. TheAudioDB uses its free public key. Every call here may throw; the callers catch it,
/// so a failing fallback can never break the page.
/// </summary>
public static class Itunes
{
    static string Big(string art) => string.IsNullOrEmpty(art) ? "" : art.Replace("100x100bb", "600x600bb").Replace("100x100", "600x600");

    static string Search(string term, string entity, string attribute, int limit) =>
        $"https://itunes.apple.com/search?term={Remote.Q(term)}&media=music&entity={entity}&attribute={attribute}&limit={limit}";

    /// <summary>Songs of the artist (an artist's own songs only), with 30 s previews. In the order iTunes ranks them.</summary>
    public static async Task<List<RemoteTrack>> TopTracksAsync(string artist, CancellationToken ct)
    {
        var list = new List<RemoteTrack>();
        using var doc = await Remote.GetJsonAsync(Search(artist, "song", "artistTerm", 40), ct);
        if (!doc.RootElement.TryGetProperty("results", out var res)) return list;
        foreach (var r in res.EnumerateArray())
        {
            string title = Remote.Str(r, "trackName"), by = Remote.Str(r, "artistName");
            if (title.Length == 0 || !Cmp.SameArtist(by, artist)) continue;
            if (list.Any(x => Cmp.TitleKey(x.Title) == Cmp.TitleKey(title))) continue;
            string preview = Remote.Str(r, "previewUrl");
            list.Add(new RemoteTrack
            {
                Title = title, Artist = by, Album = Remote.Str(r, "collectionName"),
                DurationSec = Math.Round(Remote.Lng(r, "trackTimeMillis") / 1000.0),
                Url = Remote.Str(r, "trackViewUrl"), AlbumUrl = Remote.Str(r, "collectionViewUrl"),
                CoverUrl = Big(Remote.Str(r, "artworkUrl100")), Source = "iTunes",
                HasPreview = preview.Length > 0, PreviewUrl = preview.Length > 0 ? preview : null, PreviewChecked = true
            });
            if (list.Count >= 30) break;
        }
        return list;
    }

    public static async Task<List<ReleaseInfo>> AlbumsAsync(string artist, CancellationToken ct)
    {
        var list = new List<ReleaseInfo>();
        using var doc = await Remote.GetJsonAsync(Search(artist, "album", "artistTerm", 40), ct);
        if (!doc.RootElement.TryGetProperty("results", out var res)) return list;
        foreach (var r in res.EnumerateArray())
        {
            string title = Remote.Str(r, "collectionName");
            if (title.Length == 0 || !Cmp.SameArtist(Remote.Str(r, "artistName"), artist)) continue;
            string date = Remote.Str(r, "releaseDate");
            list.Add(new ReleaseInfo
            {
                Title = title, CoverUrl = Big(Remote.Str(r, "artworkUrl100")), Url = Remote.Str(r, "collectionViewUrl"),
                Date = date.Length >= 10 ? date.Substring(0, 10) : date,
                Type = title.EndsWith("- Single", StringComparison.OrdinalIgnoreCase) ? "single" : Remote.Str(r, "collectionType").ToLowerInvariant()
            });
        }
        return list.GroupBy(x => Cmp.Key(x.Title)).Select(g => g.First()).OrderByDescending(x => x.Date).Take(8).ToList();
    }

    /// <summary>A preview for one specific song (used when Deezer has none or is unreachable).</summary>
    public static async Task<RemoteTrack> FindTrackAsync(string artist, string title, CancellationToken ct)
    {
        using var doc = await Remote.GetJsonAsync(Search(artist + " " + title, "song", "songTerm", 15), ct);
        if (!doc.RootElement.TryGetProperty("results", out var res)) return null;
        string want = Cmp.TitleKey(title);
        foreach (var r in res.EnumerateArray())
        {
            string t = Cmp.TitleKey(Remote.Str(r, "trackName"));
            bool titleOk = t == want || (want.Length > 3 && t.StartsWith(want, StringComparison.Ordinal));
            string preview = Remote.Str(r, "previewUrl");
            if (!titleOk || preview.Length == 0 || !Cmp.SameArtist(Remote.Str(r, "artistName"), artist)) continue;
            return new RemoteTrack
            {
                Title = Remote.Str(r, "trackName"), Artist = Remote.Str(r, "artistName"), Album = Remote.Str(r, "collectionName"),
                DurationSec = 30, Url = Remote.Str(r, "trackViewUrl"), AlbumUrl = Remote.Str(r, "collectionViewUrl"),
                CoverUrl = Big(Remote.Str(r, "artworkUrl100")), Source = "iTunes", HasPreview = true, PreviewUrl = preview, PreviewChecked = true
            };
        }
        return null;
    }
}

/// <summary>TheAudioDB (free public key): short biography, genre, country and the year the artist started.</summary>
public static class AudioDb
{
    public static async Task FillInfoAsync(ArtistInfo info, string name, CancellationToken ct)
    {
        using var doc = await Remote.GetJsonAsync($"https://www.theaudiodb.com/api/v1/json/2/search.php?s={Remote.Q(name)}", ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("artists", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (var a in arr.EnumerateArray())
        {
            if (!Cmp.SameArtist(Remote.Str(a, "strArtist"), name)) continue;
            if (string.IsNullOrWhiteSpace(info.Bio))
            {
                string bio = Remote.Str(a, "strBiographyEN").Trim();
                if (bio.Length > 0) { info.Bio = bio.Length > 4000 ? bio.Substring(0, 4000).TrimEnd() + "…" : bio; info.BioSource = "TheAudioDB"; }
            }
            if (info.Tags.Count == 0)
                info.Tags = new[] { Remote.Str(a, "strGenre"), Remote.Str(a, "strStyle"), Remote.Str(a, "strMood") }
                    .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
            if (info.BeginYear == 0 && int.TryParse(Remote.Str(a, "intFormedYear"), out int y) && y > 1800) info.BeginYear = y;
            if (string.IsNullOrEmpty(info.Country)) info.Country = Remote.Str(a, "strCountryCode");
            return;
        }
    }
}

/// <summary>
/// SoundCloud through its OFFICIAL API. It only works when you enter your own API app credentials in Settings
/// (soundcloud.com/you/apps - SoundCloud approves those individually). Without them nothing here runs.
/// Gives the profile text of the uploader and the public tracks; it never streams or downloads audio.
/// </summary>
public static class SoundCloud
{
    static string _token; static DateTime _tokenUntil;
    static readonly SemaphoreSlim Gate = new(1, 1);

    public static bool Configured =>
        !string.IsNullOrWhiteSpace(Remote.S.SoundCloudClientId) && !string.IsNullOrWhiteSpace(Remote.S.SoundCloudClientSecret);

    static async Task<string> TokenAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            if (_token != null && DateTime.UtcNow < _tokenUntil) return _token;
            string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(Remote.S.SoundCloudClientId + ":" + Remote.S.SoundCloudClientSecret));
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://secure.soundcloud.com/oauth/token")
            {
                Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("grant_type", "client_credentials") })
            };
            req.Headers.TryAddWithoutValidation("Authorization", "Basic " + basic);
            req.Headers.TryAddWithoutValidation("Accept", "application/json; charset=utf-8");
            try
            {
                using var resp = await Remote.Http.SendAsync(req, ct);
                Remote.Trace("HTTP", ("provider", "SoundCloud"), ("url", "oauth/token"), ("status", (int)resp.StatusCode));
                if ((int)resp.StatusCode is 400 or 401 or 403) throw new RemoteException(RemoteState.Failed, "HTTP " + (int)resp.StatusCode, RemoteKind.Auth, "SoundCloud");
                resp.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                _token = Remote.Str(doc.RootElement, "access_token");
                long secs = Math.Max(120, Remote.Lng(doc.RootElement, "expires_in"));
                _tokenUntil = DateTime.UtcNow.AddSeconds(secs - 60);
                if (string.IsNullOrEmpty(_token)) throw new RemoteException(RemoteState.Failed, "no token", RemoteKind.BadResponse, "SoundCloud");
                return _token;
            }
            catch (HttpRequestException ex) { throw new RemoteException(RemoteState.Failed, ex.Message, RemoteKind.Network, "SoundCloud"); }
        }
        finally { Gate.Release(); }
    }

    public sealed class Profile
    {
        public long Id; public string Name = "", Url = "", Bio = "", City = "", Country = "";
        public long Followers;
    }

    public static async Task<Profile> FindAsync(string name, CancellationToken ct)
    {
        if (!Configured) return null;
        string auth = "OAuth " + await TokenAsync(ct);
        using var doc = await Remote.GetJsonAsync($"https://api.soundcloud.com/users?q={Remote.Q(name)}&limit=10", ct, false, auth);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        Profile best = null; long bestFollowers = -1;
        foreach (var u in doc.RootElement.EnumerateArray())
        {
            string uname = Remote.Str(u, "username"), full = Remote.Str(u, "full_name");
            if (!Cmp.SameArtist(uname, name) && !Cmp.SameArtist(full, name)) continue;
            long f = Remote.Lng(u, "followers_count");
            if (f > bestFollowers)
            {
                bestFollowers = f;
                best = new Profile
                {
                    Id = Remote.Lng(u, "id"), Name = uname.Length > 0 ? uname : full, Url = Remote.Str(u, "permalink_url"),
                    Bio = Remote.Str(u, "description").Trim(), City = Remote.Str(u, "city"), Country = Remote.Str(u, "country_code"), Followers = f
                };
            }
        }
        return best;
    }

    public static async Task FillInfoAsync(ArtistInfo info, string name, CancellationToken ct)
    {
        var p = await FindAsync(name, ct);
        if (p == null) return;
        if (string.IsNullOrWhiteSpace(info.Bio) && p.Bio.Length > 0)
        { info.Bio = p.Bio.Length > 4000 ? p.Bio.Substring(0, 4000).TrimEnd() + "…" : p.Bio; info.BioSource = "SoundCloud"; }
        if (string.IsNullOrEmpty(info.Country)) info.Country = p.Country;
        if (info.Listeners == 0) info.Listeners = p.Followers;
    }

    /// <summary>Public tracks of the uploader (links to the SoundCloud pages; no audio is fetched).</summary>
    public static async Task<List<RemoteTrack>> TracksAsync(string name, CancellationToken ct)
    {
        var list = new List<RemoteTrack>();
        var p = await FindAsync(name, ct);
        if (p == null) return list;
        string auth = "OAuth " + await TokenAsync(ct);
        using var doc = await Remote.GetJsonAsync($"https://api.soundcloud.com/users/{p.Id}/tracks?limit=30&linked_partitioning=false", ct, false, auth);
        var arr = doc.RootElement;
        if (arr.ValueKind == JsonValueKind.Object && arr.TryGetProperty("collection", out var c)) arr = c;
        if (arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var t in arr.EnumerateArray())
        {
            string title = Remote.Str(t, "title");
            if (title.Length == 0) continue;
            string art = Remote.Str(t, "artwork_url");
            list.Add(new RemoteTrack
            {
                Title = title, Artist = name, Plays = Remote.Lng(t, "playback_count"), DurationSec = Math.Round(Remote.Lng(t, "duration") / 1000.0),
                Url = Remote.Str(t, "permalink_url"), CoverUrl = art.Replace("-large", "-t500x500"), Source = "SoundCloud",
                HasPreview = false, PreviewChecked = true                 // SoundCloud audio is not played inside Vibe
            });
        }
        return list.OrderByDescending(x => x.Plays).ToList();
    }
}
