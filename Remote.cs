using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PortablePlayer;

// ---------------------------------------------------------------------------------------------- shared types
/// <summary>Why a remote section looks the way it does. "No data" and "request failed" are never mixed up.</summary>
public enum RemoteState { NotLoaded, Loading, Ok, NoData, Failed, Offline, Disabled }

/// <summary>What actually went wrong. The UI shows a different, specific message for each kind.</summary>
public enum RemoteKind { None, Offline, Network, Auth, RateLimit, Server, Timeout, BadResponse, Other }

public sealed class RemoteException : Exception
{
    public RemoteState State { get; }
    public RemoteKind Kind { get; }
    public string Provider { get; }

    public RemoteException(RemoteState state, string message, RemoteKind kind = RemoteKind.Other, string provider = "")
        : base(message) { State = state; Kind = kind; Provider = provider ?? ""; }

    static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    /// <summary>User-facing text. <paramref name="what"/> is a plural noun phrase such as "recommendations" or "popular tracks".</summary>
    public string Describe(string what)
    {
        string who = string.IsNullOrEmpty(Provider) ? "the music service" : Provider;
        return Kind switch
        {
            RemoteKind.Offline => $"You're offline. Cached {what} may still be available.",
            RemoteKind.Network => $"Couldn't reach {who}. {Cap(what)} are temporarily unavailable.",
            RemoteKind.Auth => $"{who} rejected the API key. Check it in Settings → Library.",
            RemoteKind.RateLimit => $"{who} rate limit reached. Try again in a minute.",
            RemoteKind.Timeout => $"{who} took too long to answer. {Cap(what)} are temporarily unavailable.",
            _ => $"{Cap(what)} are temporarily unavailable."
        };
    }
}

/// <summary>One remote section of the artist profile (popular tracks, similar artists, ...). Message = a note or the failure text.</summary>
public sealed class Section<T>
{
    public RemoteState State { get; set; } = RemoteState.NotLoaded;
    public string Message { get; set; } = "";
    public List<T> Items { get; set; } = new();
}

/// <summary>A song known from an online service. It may or may not exist in the local library.</summary>
public sealed class RemoteTrack
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public long Plays { get; set; }
    public double DurationSec { get; set; }
    public string Url { get; set; } = "";          // page of the track on the service (for "Open online")
    public string AlbumUrl { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public string Source { get; set; } = "";
    public long DeezerId { get; set; }
    /// <summary>A 30 s preview exists. The URL itself is short-lived, so it is never cached (see AudioProviderService).</summary>
    public bool HasPreview { get; set; }
    /// <summary>Similarity to the seed song / artist (0..1), when the source provides one.</summary>
    public double Score { get; set; }
    [JsonIgnore] public string PreviewUrl { get; set; }
    /// <summary>The audio provider has been asked whether there is a preview (so "no preview" is a known fact, not a guess).</summary>
    [JsonIgnore] public bool PreviewChecked { get; set; }
    [JsonIgnore] public string Key => Cmp.ArtistKey(Artist) + "|" + Cmp.TitleKey(Title);
}

public sealed class RelatedArtist
{
    public string Name { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    /// <summary>Similarity 0..1 when the source provides one (0 = unknown).</summary>
    public double Match { get; set; }
    public string Source { get; set; } = "";
}

public sealed class ReleaseInfo
{
    public string Title { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public string Url { get; set; } = "";
    public string Date { get; set; } = "";
    public string Type { get; set; } = "";
}

public sealed class DeezerArtist
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Picture { get; set; } = "";
    public string Link { get; set; } = "";
    public long Fans { get; set; }
}

// ---------------------------------------------------------------------------------------------- shared plumbing
/// <summary>HTTP, JSON helpers, rate limiting and error classification for every online service.</summary>
public static class Remote
{
    public static readonly HttpClient Http = CreateClient();
    static readonly SemaphoreSlim MbLock = new(1, 1);
    static DateTime _lastMb = DateTime.MinValue;

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("VibeMusicPlayer/1.0 (desktop app)");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return c;
    }

    public static AppSettings S => App.Instance.Settings;
    public static bool Enabled => S.OnlineArtistInfo;
    public static bool HasLastFm => !string.IsNullOrWhiteSpace(S.LastFmApiKey);

    public static string ArtistDir => Directory.CreateDirectory(Path.Combine(Paths.DataDir, "cache", "artists")).FullName;
    public static string ImageDir => Directory.CreateDirectory(Path.Combine(Paths.DataDir, "cache", "images")).FullName;

    public static string Q(string s) => Uri.EscapeDataString(s ?? "");

    public static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static long Lng(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var m) ? m : 0;
    }

    public static double Dbl(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        return v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    public static string Hash(string s)
    {
        using var sha = SHA1.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s))).Substring(0, 20).ToLowerInvariant();
    }

    // ------------------------------------------------------------------ discovery log (data\discovery.log)
    static readonly object LogLock = new();
    public static string LogFile => Path.Combine(Paths.DataDir, "discovery.log");

    /// <summary>One line per request / lookup, so a failure can be traced to its real cause. API keys are never written.</summary>
    public static void Trace(string component, params (string Key, object Value)[] fields)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append('[').Append(DateTime.Now.ToString("s")).Append("] ").Append(component);
            foreach (var (k, v) in fields) sb.Append(' ').Append(k).Append('=').Append(v is string str && str.Contains(' ') ? $"\"{str}\"" : v ?? "");
            lock (LogLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile));
                var fi = new FileInfo(LogFile);
                if (fi.Exists && fi.Length > 600_000) { try { File.Copy(LogFile, LogFile + ".old", true); File.Delete(LogFile); } catch { } }
                File.AppendAllText(LogFile, sb.AppendLine().ToString());
            }
        }
        catch { /* logging must never break anything */ }
    }

    static string Redact(string url) => System.Text.RegularExpressions.Regex.Replace(url, @"(api_key=)[^&]+", "$1***");

    public static string ProviderFor(string host)
    {
        host = (host ?? "").ToLowerInvariant();
        if (host.Contains("audioscrobbler") || host.Contains("last.fm")) return "Last.fm";
        if (host.Contains("deezer")) return "Deezer";
        if (host.Contains("itunes") || host.Contains("apple.com")) return "iTunes";
        if (host.Contains("soundcloud")) return "SoundCloud";
        if (host.Contains("musicbrainz")) return "MusicBrainz";
        if (host.Contains("listenbrainz")) return "ListenBrainz";
        if (host.Contains("theaudiodb")) return "TheAudioDB";
        if (host.Contains("fanart")) return "Fanart.tv";
        if (host.Contains("wikipedia") || host.Contains("wikimedia")) return "Wikipedia";
        if (host.Contains("wikidata")) return "Wikidata";
        return host;
    }

    static RemoteException FromHttp(HttpRequestException ex, string provider)
    {
        if (ex.StatusCode is { } code)
        {
            int c = (int)code;
            if (c is 401 or 403) return new RemoteException(RemoteState.Failed, $"HTTP {c}", RemoteKind.Auth, provider);
            if (c == 429) return new RemoteException(RemoteState.Failed, "HTTP 429", RemoteKind.RateLimit, provider);
            return new RemoteException(RemoteState.Failed, $"HTTP {c}", RemoteKind.Server, provider);
        }
        // no HTTP answer at all: DNS / connection / TLS. "Offline" only when the machine really has no network.
        return System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()
            ? new RemoteException(RemoteState.Failed, ex.InnerException?.Message ?? ex.Message, RemoteKind.Network, provider)
            : new RemoteException(RemoteState.Offline, "no network", RemoteKind.Offline, provider);
    }

    /// <summary>GET + parse. MusicBrainz calls are spaced 1.1 s apart; a 429/503 is retried once. Every request is logged.</summary>
    public static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct, bool musicBrainz = false, string auth = null)
    {
        string host = new Uri(url).Host, provider = ProviderFor(host), shown = Redact(url);
        for (int attempt = 0; ; attempt++)
        {
            if (musicBrainz)
            {
                await MbLock.WaitAsync(ct);
                try
                {
                    var wait = _lastMb + TimeSpan.FromMilliseconds(1100) - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                    _lastMb = DateTime.UtcNow;
                }
                finally { MbLock.Release(); }
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(auth)) req.Headers.TryAddWithoutValidation("Authorization", auth);
                using var resp = await Http.SendAsync(req, ct);
                Trace("HTTP", ("provider", provider), ("url", shown), ("status", (int)resp.StatusCode), ("ms", sw.ElapsedMilliseconds));
                if ((int)resp.StatusCode is 429 or 503 && attempt == 0)
                {
                    var ra = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2.5);
                    await Task.Delay(ra > TimeSpan.FromSeconds(6) ? TimeSpan.FromSeconds(6) : ra, ct);
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            }
            catch (HttpRequestException ex)
            {
                Trace("HTTP-FAIL", ("provider", provider), ("url", shown), ("status", ex.StatusCode?.ToString() ?? "none"),
                      ("error", ex.GetType().Name), ("detail", ex.InnerException?.Message ?? ex.Message));
                throw FromHttp(ex, provider);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                Trace("HTTP-FAIL", ("provider", provider), ("url", shown), ("error", "timeout"), ("ms", sw.ElapsedMilliseconds));
                throw new RemoteException(RemoteState.Failed, "timeout", RemoteKind.Timeout, provider);
            }
            catch (JsonException ex)
            {
                Trace("HTTP-FAIL", ("provider", provider), ("url", shown), ("error", "bad-json"), ("detail", ex.Message));
                throw new RemoteException(RemoteState.Failed, "unexpected answer", RemoteKind.BadResponse, provider);
            }
        }
    }

    // ------------------------------------------------------------------ error classification
    public static RemoteException Classify(Exception ex)
    {
        if (ex is RemoteException re) return re;
        if (ex is HttpRequestException h) return FromHttp(h, "");
        if (ex is TaskCanceledException or TimeoutException) return new RemoteException(RemoteState.Failed, "timeout", RemoteKind.Timeout);
        if (ex is JsonException) return new RemoteException(RemoteState.Failed, "unexpected answer", RemoteKind.BadResponse);
        Trace("UNEXPECTED", ("error", ex.GetType().Name), ("detail", ex.Message));
        return new RemoteException(RemoteState.Failed, ex.Message, RemoteKind.Other);
    }

    // ------------------------------------------------------------------ Last.fm
    public static string LastFmUrl(string method, string args) =>
        $"https://ws.audioscrobbler.com/2.0/?method={method}&{args}&api_key={Q(S.LastFmApiKey)}&format=json";

    /// <summary>Returns false when Last.fm says "not found" (a normal empty answer); throws for real errors.</summary>
    public static bool LastFmOk(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var e)) return true;
        long code = e.ValueKind == JsonValueKind.Number ? e.GetInt64() : 0;
        if (code == 6) return false;                                  // artist / track not found
        Trace("Last.fm-API-ERROR", ("code", code), ("message", Str(root, "message")));
        if (code is 4 or 10 or 26) throw new RemoteException(RemoteState.Failed, $"api error {code}", RemoteKind.Auth, "Last.fm");
        if (code is 29) throw new RemoteException(RemoteState.Failed, "api error 29", RemoteKind.RateLimit, "Last.fm");
        throw new RemoteException(RemoteState.Failed, $"api error {code}", RemoteKind.Server, "Last.fm");
    }

    // ------------------------------------------------------------------ Deezer (keyless)
    public static void DeezerCheck(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var e))
        {
            long code = Lng(e, "code");
            Trace("Deezer-API-ERROR", ("code", code), ("type", Str(e, "type")), ("message", Str(e, "message")));
            throw new RemoteException(RemoteState.Failed, $"api error {code}", code == 4 ? RemoteKind.RateLimit : RemoteKind.Server, "Deezer");
        }
    }

    static string Plain(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in (s ?? "").ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Finds the artist on Deezer: exact name first, then a careful fuzzy match (never a different artist).</summary>
    static readonly Dictionary<string, DeezerArtist> DeezerMemo = new();

    public static async Task<DeezerArtist> DeezerFindAsync(string name, CancellationToken ct)
    {
        string memoKey = Cmp.ArtistKey(name);
        lock (DeezerMemo) if (DeezerMemo.TryGetValue(memoKey, out var memo)) return memo;
        var found = await DeezerFindCoreAsync(name, ct);
        if (found != null) lock (DeezerMemo) DeezerMemo[memoKey] = found;
        return found;
    }

    static async Task<DeezerArtist> DeezerFindCoreAsync(string name, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"https://api.deezer.com/search/artist?q={Q(name)}&limit=8", ct);
        DeezerCheck(doc.RootElement);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return null;

        DeezerArtist Make(JsonElement a) => new()
        {
            Id = Lng(a, "id"), Name = Str(a, "name"), Link = Str(a, "link"), Fans = Lng(a, "nb_fan"), Picture = BestPicture(a)
        };

        foreach (var a in data.EnumerateArray()) if (Cmp.SameArtist(Str(a, "name"), name)) return Make(a);
        string want = Plain(Cmp.ArtistKey(name));
        foreach (var a in data.EnumerateArray())               // punctuation / accents differences ("P!nk", "Beyonce")
            if (Plain(Cmp.ArtistKey(Str(a, "name"))) == want && want.Length > 1) return Make(a);
        return null;
    }

    public static string BestPicture(JsonElement a)
    {
        foreach (var k in new[] { "picture_xl", "picture_big", "picture_medium" })
        {
            string u = Str(a, k);
            if (u.Length > 0 && !u.Contains("/images/artist//")) return u;
        }
        return "";
    }

    public static RemoteTrack DeezerTrack(JsonElement t)
    {
        long albumId = 0; string album = "", cover = "";
        if (t.TryGetProperty("album", out var al))
        {
            album = Str(al, "title");
            albumId = Lng(al, "id");
            cover = Str(al, "cover_big"); if (cover.Length == 0) cover = Str(al, "cover_medium");
        }
        string artist = t.TryGetProperty("artist", out var ar) ? Str(ar, "name") : "";
        string preview = Str(t, "preview");
        return new RemoteTrack
        {
            Title = Str(t, "title"), Artist = artist, Album = album, DurationSec = Lng(t, "duration"),
            Url = Str(t, "link"), AlbumUrl = albumId > 0 ? $"https://www.deezer.com/album/{albumId}" : "",
            CoverUrl = cover, Source = "Deezer", DeezerId = Lng(t, "id"),
            HasPreview = preview.Length > 0, PreviewUrl = preview.Length > 0 ? preview : null, PreviewChecked = true     // Deezer tells us for sure
        };
    }

    // ------------------------------------------------------------------ pictures on disk
    static readonly TimeSpan ImageTtl = TimeSpan.FromDays(30);

    /// <summary>Downloads a picture once and keeps it in the cache folder. Returns the local file, or null.</summary>
    public static async Task<string> FetchImageFileAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        string file = Path.Combine(ImageDir, Hash(url) + ".img");
        try
        {
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < ImageTtl && new FileInfo(file).Length > 200) return file;
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length < 200 || bytes.Length > 12_000_000) return null;
            await File.WriteAllBytesAsync(file, bytes, ct);
            return file;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            try { if (File.Exists(file) && new FileInfo(file).Length < 200) File.Delete(file); } catch { }
            return null;
        }
    }

    // ---- 30 s previews: downloaded to a small cache file first, so the audio engine only ever opens a LOCAL file
    public static string PreviewDir => Directory.CreateDirectory(Path.Combine(Paths.DataDir, "cache", "previews")).FullName;

    /// <summary>Downloads a preview clip (max ~3 MB). Returns the local file, or throws a classifiable exception.</summary>
    public static async Task<string> FetchPreviewFileAsync(string url, CancellationToken ct)
    {
        string ext = ".mp3";
        try { string e = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant(); if (e is ".m4a" or ".mp3" or ".aac" or ".mp4") ext = e; } catch { }
        string file = Path.Combine(PreviewDir, Hash(url) + ext);
        if (File.Exists(file) && new FileInfo(file).Length > 4000) return file;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
        if (bytes.Length < 4000 || bytes.Length > 6_000_000) throw new InvalidDataException("The preview file is empty.");
        await File.WriteAllBytesAsync(file, bytes, ct);
        TrimPreviews();
        return file;
    }

    static void TrimPreviews()
    {
        try
        {
            foreach (var f in new DirectoryInfo(PreviewDir).GetFiles().OrderByDescending(x => x.LastWriteTimeUtc).Skip(40))
                try { f.Delete(); } catch { }
        }
        catch { }
    }
}
