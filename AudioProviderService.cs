using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PortablePlayer;

public sealed class AudioSource
{
    public string Url { get; init; }
    public string Provider { get; init; }
    /// <summary>"preview" (30 s clip). Full-length playback / downloads have no authorized free source.</summary>
    public string Kind { get; init; }
    public int Seconds { get; init; }
}

/// <summary>
/// Audio is deliberately separate from metadata. The only audio the free services hand out legally is a 30-second preview
/// (Deezer). There is no authorized full-length stream and no authorized download, so those return null and the UI says so -
/// nothing is scraped and nothing is pretended.
/// </summary>
public sealed class AudioProviderService
{
    public static AudioProviderService Instance { get; } = new();

    /// <summary>A fresh preview URL (they are short-lived, so they are looked up when needed, never cached).</summary>
    public async Task<AudioSource> GetPreviewSourceAsync(RemoteTrack t, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(t.PreviewUrl)) return Make(t.PreviewUrl, t.Source);

        // Deezer first; when it is unreachable or has no preview, iTunes is asked (also keyless, also 30 s previews)
        RemoteException deezerError = null;
        try
        {
            var dz = await DeezerPreviewAsync(t, ct);
            if (dz != null) return dz;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { deezerError = Remote.Classify(ex); }

        try
        {
            var it = await Itunes.FindTrackAsync(t.Artist, t.Title, ct);
            if (it != null)
            {
                t.PreviewUrl = it.PreviewUrl; t.HasPreview = true;
                if (string.IsNullOrEmpty(t.CoverUrl)) t.CoverUrl = it.CoverUrl;
                if (string.IsNullOrEmpty(t.Album)) t.Album = it.Album;
                if (string.IsNullOrEmpty(t.Url)) t.Url = it.Url;
                return Make(it.PreviewUrl, "iTunes");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { deezerError ??= Remote.Classify(ex); }

        if (deezerError != null) throw deezerError;                  // both failed: report it (not "no preview")
        t.HasPreview = false;
        return null;
    }

    async Task<AudioSource> DeezerPreviewAsync(RemoteTrack t, CancellationToken ct)
    {
        if (t.DeezerId > 0)
        {
            using var doc = await Remote.GetJsonAsync($"https://api.deezer.com/track/{t.DeezerId}", ct);
            Remote.DeezerCheck(doc.RootElement);
            string p = Remote.Str(doc.RootElement, "preview");
            if (p.Length > 0) { t.PreviewUrl = p; t.HasPreview = true; return Make(p, "Deezer"); }
        }

        string q = $"artist:\"{t.Artist.Replace("\"", " ")}\" track:\"{t.Title.Replace("\"", " ")}\"";
        using var doc2 = await Remote.GetJsonAsync($"https://api.deezer.com/search?q={Remote.Q(q)}&limit=6", ct);
        Remote.DeezerCheck(doc2.RootElement);
        if (doc2.RootElement.TryGetProperty("data", out var data))
            foreach (var item in data.EnumerateArray())
            {
                var rt = Remote.DeezerTrack(item);
                string a = Cmp.TitleKey(rt.Title), b = Cmp.TitleKey(t.Title);
                bool titleOk = a == b || (b.Length > 3 && a.StartsWith(b, StringComparison.Ordinal));
                if (!titleOk || !Cmp.SameArtist(rt.Artist, t.Artist) || string.IsNullOrEmpty(rt.PreviewUrl)) continue;
                t.PreviewUrl = rt.PreviewUrl; t.HasPreview = true; t.DeezerId = rt.DeezerId;
                if (string.IsNullOrEmpty(t.CoverUrl)) t.CoverUrl = rt.CoverUrl;
                if (string.IsNullOrEmpty(t.Album)) t.Album = rt.Album;
                if (string.IsNullOrEmpty(t.AlbumUrl)) t.AlbumUrl = rt.AlbumUrl;
                if (t.DurationSec <= 0) t.DurationSec = rt.DurationSec;
                if (string.IsNullOrEmpty(t.Url)) t.Url = rt.Url;
                return Make(rt.PreviewUrl, "Deezer");
            }
        return null;
    }

    static AudioSource Make(string url, string provider) => new() { Url = url, Provider = string.IsNullOrEmpty(provider) ? "Deezer" : provider, Kind = "preview", Seconds = 30 };

    /// <summary>Full-length online playback: no authorized free provider exists, so this is always null.</summary>
    public Task<AudioSource> GetPlayableSourceAsync(RemoteTrack t, CancellationToken ct) => Task.FromResult<AudioSource>(null);

    /// <summary>Authorized download: none of the free services offers one, so this is always null (a preview is not a download).</summary>
    public Task<AudioSource> GetDownloadSourceAsync(RemoteTrack t, CancellationToken ct) => Task.FromResult<AudioSource>(null);

    /// <summary>A normal web page for the song ("Open online"). Only a link, nothing is fetched from it.</summary>
    public string OnlinePageUrl(RemoteTrack t) =>
        !string.IsNullOrEmpty(t.Url) ? t.Url : $"https://www.last.fm/music/{Remote.Q(t.Artist)}/_/{Remote.Q(t.Title)}";

    /// <summary>A transient track that the normal player can play (it is never added to the library or saved).</summary>
    public async Task<Track> CreatePreviewTrackAsync(RemoteTrack t, CancellationToken ct)
    {
        var src = await GetPreviewSourceAsync(t, ct);
        if (src == null) return null;

        // The clip is downloaded first and played from a local file. Preview links are short-lived: when the stored one
        // has expired, ask for a fresh one once and try again.
        string file;
        try { file = await Remote.FetchPreviewFileAsync(src.Url, ct); }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException || ex is System.IO.InvalidDataException)
        {
            t.PreviewUrl = null;
            src = await GetPreviewSourceAsync(t, ct);
            if (src == null) return null;
            file = await Remote.FetchPreviewFileAsync(src.Url, ct);
        }

        var track = new Track
        {
            FilePath = file, Title = t.Title, Artist = t.Artist,
            Album = string.IsNullOrEmpty(t.Album) ? "Online preview" : t.Album,
            DurationSec = src.Seconds, IsOnline = true, OnlineUrl = OnlinePageUrl(t)
        };
        if (!string.IsNullOrEmpty(t.CoverUrl))
        {
            try { track.OnlineArt = await ArtistImageService.Instance.LoadAsync(t.CoverUrl, 512, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
        }
        return track;
    }
}
