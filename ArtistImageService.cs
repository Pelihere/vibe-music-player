using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PortablePlayer;

/// <summary>One picture of the artist, exactly as the file is (nothing is processed).</summary>
public sealed class HeroImage
{
    /// <summary>Full path of the picture file in the artist pictures folder.</summary>
    public string Url { get; set; } = "";
    /// <summary>Cutout (the file has a transparent background) | Portrait | Square | Landscape</summary>
    public string Kind { get; set; } = "Portrait";
    public int Width { get; set; }
    public int Height { get; set; }
    public string Source { get; set; } = "Local";
}

/// <summary>
/// The artist pictures are a local database: a folder with one cutout per artist, named after the artist.
///   data\artist-pictures\Frank Ocean.png        (more pictures: "Frank Ocean (2).png", "Frank Ocean_3.png" ... or a folder "Frank Ocean")
/// The player only matches the artist's name to the file name and shows the picture - no downloading, no processing.
/// Matching ignores capitalization, spacing, punctuation, accents and a leading "The" ("The Weeknd" = "the weeknd" = "Weeknd.png").
/// Up to five pictures per artist rotate on the artist page.
/// </summary>
public sealed class ArtistImageService
{
    public static ArtistImageService Instance { get; } = new();

    const int MaxImages = 5;
    static readonly string[] Extensions = { ".png", ".webp", ".jpg", ".jpeg", ".bmp" };

    /// <summary>The folder the user fills with artist pictures (created on demand, next to the other Vibe data).</summary>
    public static string PicturesDir
    {
        get
        {
            string dir = Path.Combine(Paths.DataDir, "artist-pictures");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                try
                {
                    File.WriteAllText(Path.Combine(dir, "README.txt"),
                        "Artist pictures for Vibe\r\n\r\n" +
                        "Put one picture per artist in this folder and name the file after the artist:\r\n" +
                        "    Frank Ocean.png\r\n    The Weeknd.png\r\n    Arctic Monkeys.png\r\n\r\n" +
                        "Transparent PNG / WebP cutouts work best. Capitalization, spacing and a leading \"The\" do not matter.\r\n" +
                        "More pictures of the same artist (up to 5 rotate on the artist page):\r\n" +
                        "    Frank Ocean (2).png    Frank Ocean (3).png    or a sub-folder named \"Frank Ocean\" with the pictures inside.\r\n");
                }
                catch { }
            }
            return dir;
        }
    }

    // ------------------------------------------------------------------ name matching
    /// <summary>Comparable form of a name: no accents, letters and digits only, lower case, without a leading "The".</summary>
    static string KeyOf(string name)
    {
        string n = Cmp.ArtistKey(name).Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (char c in n)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    // "Name (2)", "Name [2]", "Name_2", "Name-2", "Name.2" - one or two digits only, so "Blink-182" stays a name of its own
    static readonly Regex Variant = new(@"^(?<n>.+?)\s*(?:\(\s*(?<i>\d{1,2})\s*\)|\[\s*(?<i>\d{1,2})\s*\]|[_\-.]\s*(?<i>\d{1,2}))$", RegexOptions.Compiled);

    /// <summary>Files of the pictures folder that belong to this artist, best match first (exact name, then numbered variants, then folder).</summary>
    public static List<string> FindFiles(string artist)
    {
        var result = new List<string>();
        string want = KeyOf(artist);
        if (want.Length == 0) return result;
        try
        {
            string root = PicturesDir;
            var exact = new List<string>();
            var numbered = new List<(int N, string File)>();
            foreach (var f in Directory.EnumerateFiles(root))
            {
                if (!Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
                string stem = Path.GetFileNameWithoutExtension(f);
                if (KeyOf(stem) == want) { exact.Add(f); continue; }
                var m = Variant.Match(stem);
                if (m.Success && KeyOf(m.Groups["n"].Value) == want) numbered.Add((int.Parse(m.Groups["i"].Value), f));
            }
            result.AddRange(exact.OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            result.AddRange(numbered.OrderBy(x => x.N).ThenBy(x => x.File, StringComparer.OrdinalIgnoreCase).Select(x => x.File));

            foreach (var dir in Directory.EnumerateDirectories(root))
                if (KeyOf(Path.GetFileName(dir)) == want)
                    result.AddRange(Directory.EnumerateFiles(dir)
                        .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) { Remote.Trace("ArtistPictures", ("artist", artist), ("failed", ex.GetType().Name), ("detail", ex.Message)); }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxImages).ToList();
    }

    /// <summary>How many picture files the folder holds (Settings).</summary>
    public int CountPictures()
    {
        try
        {
            return Directory.EnumerateFiles(PicturesDir, "*", SearchOption.AllDirectories)
                            .Count(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        }
        catch { return 0; }
    }

    /// <summary>A file name that is safe on Windows and matches this artist (used in hints).</summary>
    public static string SuggestedFileName(string artist) =>
        new string((artist ?? "").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()) + ".png";

    // ------------------------------------------------------------------ getArtistImages
    /// <summary>
    /// The pictures of this artist from the local folder (0-5). Reads only the image headers to learn size and transparency.
    /// Never throws and never touches the network, so it works offline and independently of every other part of the artist page.
    /// </summary>
    public async Task<List<HeroImage>> GetArtistImagesAsync(string name, ArtistInfo info, DeezerArtist deezer, CancellationToken ct, bool remote = false)
    {
        var list = await LocalImagesAsync(name, ct);                // the local database always wins
        if (list.Count > 0 || !remote || !Remote.Enabled) return list;
        try { return await RemoteImagesAsync(name, info, deezer, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Remote.Trace("ArtistPictures", ("artist", name), ("api", "failed"), ("detail", ex.Message));
            return new List<HeroImage>();
        }
    }

    Task<List<HeroImage>> LocalImagesAsync(string name, CancellationToken ct) =>
        Task.Run(() =>
        {
            var list = new List<HeroImage>();
            foreach (var file in FindFiles(name))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var fs = File.OpenRead(file);
                    var frame = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                    var fmt = frame.Format;
                    bool alpha = fmt == PixelFormats.Bgra32 || fmt == PixelFormats.Pbgra32 || fmt == PixelFormats.Rgba64 || fmt == PixelFormats.Prgba64
                                 || fmt == PixelFormats.Rgba128Float || fmt == PixelFormats.Prgba128Float;
                    double ratio = (double)frame.PixelWidth / Math.Max(1, frame.PixelHeight);
                    list.Add(new HeroImage
                    {
                        Url = file, Source = "Local", Width = frame.PixelWidth, Height = frame.PixelHeight,
                        Kind = alpha ? "Cutout" : ratio >= 1.4 ? "Landscape" : ratio <= 0.95 ? "Portrait" : "Square"
                    });
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Remote.Trace("ArtistPictures", ("file", Path.GetFileName(file)), ("unreadable", ex.GetType().Name)); }
            }
            Remote.Trace("ArtistPictures", ("artist", name), ("local", list.Count));
            return list;
        }, ct);

    // ------------------------------------------------------------------ API fallback (only when the local folder has nothing)
    const int MinSide = 450;
    static string SetFile(string artist) => Path.Combine(Remote.ImageDir, "set-" + KeyOf(artist) + ".json");

    sealed class SavedSet { public DateTime At { get; set; } public List<SavedImage> Images { get; set; } = new(); }
    sealed class SavedImage { public string Url { get; set; } = ""; public string Source { get; set; } = ""; }

    /// <summary>
    /// Up to five pictures from the free providers (TheAudioDB, Wikimedia Commons, Wikipedia, Deezer), exactly as served.
    /// Each provider is tried on its own, so one failing never blocks the others. The chosen set is remembered for 7 days.
    /// </summary>
    async Task<List<HeroImage>> RemoteImagesAsync(string name, ArtistInfo info, DeezerArtist deezer, CancellationToken ct)
    {
        var result = new List<HeroImage>();
        var seen = new HashSet<long>();

        // remembered set
        try
        {
            string sf = SetFile(name);
            if (File.Exists(sf) && DateTime.UtcNow - File.GetLastWriteTimeUtc(sf) < TimeSpan.FromDays(7))
            {
                var saved = JsonSerializer.Deserialize<SavedSet>(File.ReadAllText(sf));
                foreach (var im in saved?.Images ?? new())
                    if (File.Exists(im.Url)) { var h = await Describe(im.Url, im.Source, ct); if (h != null) result.Add(h); }
                if (result.Count > 0) return result;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { result.Clear(); }

        async Task Add(string url, string source)
        {
            if (result.Count >= MaxImages || string.IsNullOrWhiteSpace(url)) return;
            string file = await Remote.FetchImageFileAsync(url, ct);
            if (file == null) return;
            var h = await Describe(file, source, ct);
            if (h == null || Math.Min(h.Width, h.Height) < MinSide) return;
            long sig = new FileInfo(file).Length;                    // the same picture served twice has the same size
            if (!seen.Add(sig)) return;
            result.Add(h);
        }

        async Task Safe(string provider, Func<Task> work)
        {
            if (result.Count >= MaxImages) return;
            try { await work(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Remote.Trace("ArtistPictures", ("artist", name), ("provider", provider), ("failed", ex.GetType().Name), ("detail", ex.Message)); }
        }

        string want = KeyOf(name);

        await Safe("TheAudioDB", async () =>
        {
            using var doc = await Remote.GetJsonAsync($"https://www.theaudiodb.com/api/v1/json/2/search.php?s={Remote.Q(name)}", ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("artists", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
            foreach (var a in arr.EnumerateArray())
            {
                bool same = KeyOf(Remote.Str(a, "strArtist")) == want ||
                            (!string.IsNullOrEmpty(info?.Mbid) && Remote.Str(a, "strMusicBrainzID").Equals(info.Mbid, StringComparison.OrdinalIgnoreCase));
                if (!same) continue;
                foreach (var f in new[] { "strArtistCutout", "strArtistClearart", "strArtistThumb", "strArtistFanart", "strArtistFanart2", "strArtistFanart3", "strArtistFanart4", "strArtistWideThumb" })
                    await Add(Remote.Str(a, f), "TheAudioDB");
                break;
            }
        });

        await Safe("Wikimedia Commons", async () =>
        {
            string qid = info?.WikidataId;
            if (string.IsNullOrWhiteSpace(qid)) return;
            using var doc = await Remote.GetJsonAsync($"https://www.wikidata.org/wiki/Special:EntityData/{Remote.Q(qid)}.json", ct);
            if (!doc.RootElement.TryGetProperty("entities", out var ents) || !ents.TryGetProperty(qid, out var ent)) return;
            if (!ent.TryGetProperty("claims", out var claims) || !claims.TryGetProperty("P18", out var p18)) return;
            foreach (var c in p18.EnumerateArray().Take(2))
            {
                if (!c.TryGetProperty("mainsnak", out var ms) || !ms.TryGetProperty("datavalue", out var dv)) continue;
                string file = Remote.Str(dv, "value");
                if (file.Length > 0) await Add($"https://commons.wikimedia.org/wiki/Special:FilePath/{Remote.Q(file)}?width=1600", "Wikimedia Commons");
            }
        });

        await Safe("Wikipedia", () => Add(info?.WikiImage, "Wikipedia"));

        await Safe("Deezer", async () =>
        {
            deezer ??= await Remote.DeezerFindAsync(name, ct);
            if (deezer != null && KeyOf(deezer.Name) == want) await Add(deezer.Picture, "Deezer");
        });

        try
        {
            if (result.Count > 0)
                File.WriteAllText(SetFile(name), JsonSerializer.Serialize(new SavedSet
                {
                    At = DateTime.UtcNow, Images = result.Select(r => new SavedImage { Url = r.Url, Source = r.Source }).ToList()
                }));
        }
        catch { }
        Remote.Trace("ArtistPictures", ("artist", name), ("api", result.Count));
        return result;
    }

    static Task<HeroImage> Describe(string file, string source, CancellationToken ct) =>
        Task.Run(() =>
        {
            try
            {
                using var fs = File.OpenRead(file);
                var frame = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                var fmt = frame.Format;
                bool alpha = fmt == PixelFormats.Bgra32 || fmt == PixelFormats.Pbgra32 || fmt == PixelFormats.Rgba64 || fmt == PixelFormats.Prgba64
                             || fmt == PixelFormats.Rgba128Float || fmt == PixelFormats.Prgba128Float;
                double ratio = (double)frame.PixelWidth / Math.Max(1, frame.PixelHeight);
                return new HeroImage
                {
                    Url = file, Source = source, Width = frame.PixelWidth, Height = frame.PixelHeight,
                    Kind = alpha ? "Cutout" : ratio >= 1.4 ? "Landscape" : ratio <= 0.95 ? "Portrait" : "Square"
                };
            }
            catch { return null; }
        }, ct);

    public void Forget(string name) { try { File.Delete(SetFile(name)); } catch { } }

    /// <summary>Removes cache files of earlier online picture experiments (the pictures folder itself is never touched).</summary>
    public void ClearSets()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Remote.ImageDir, "set-*.json")) File.Delete(f);
        }
        catch { }
    }

    public static void CleanupLegacyFiles()
    {
        foreach (var dir in new[] { Path.Combine(Paths.DataDir, "cache", "cutouts"), Path.Combine(Paths.DataDir, "models"), Path.Combine(Paths.DataDir, "cache", "artist-images") })
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        try { _ = PicturesDir; } catch { }                    // make sure the folder exists so it can be found and filled
    }

    // ------------------------------------------------------------------ small square portraits for related-artist cards (unchanged)
    static Dictionary<string, string> _urls;
    static readonly object Lock = new();
    static string MapFile => Path.Combine(Paths.DataDir, "cache", "artist-images.json");

    static Dictionary<string, string> Map()
    {
        lock (Lock)
        {
            if (_urls != null) return _urls;
            try { if (File.Exists(MapFile)) _urls = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(MapFile)); }
            catch { _urls = null; }
            return _urls ??= new Dictionary<string, string>();
        }
    }

    public void ClearMap() { lock (Lock) _urls = null; try { File.Delete(MapFile); } catch { } }

    public async Task<string> GetBestArtistImageAsync(string name, CancellationToken ct)
    {
        string key = Cmp.ArtistKey(name);
        var map = Map();
        lock (Lock) if (map.TryGetValue(key, out var hit)) return hit;
        var dz = await Remote.DeezerFindAsync(name, ct);
        string url = dz?.Picture ?? "";
        lock (Lock)
        {
            map[key] = url;
            try { Directory.CreateDirectory(Path.GetDirectoryName(MapFile)); File.WriteAllText(MapFile, JsonSerializer.Serialize(map)); } catch { }
        }
        return url;
    }

    // ------------------------------------------------------------------ decoding for the UI
    /// <summary>Decodes a picture. A local file path is read directly; a web address (related-artist cards) goes through the download cache.</summary>
    public async Task<BitmapSource> LoadAsync(string urlOrFile, int decodeWidth, CancellationToken ct)
    {
        string file = File.Exists(urlOrFile) ? urlOrFile : await Remote.FetchImageFileAsync(urlOrFile, ct);
        if (file == null) return null;
        try
        {
            return await Task.Run(() =>
            {
                int srcWidth;
                using (var fs = File.OpenRead(file))
                    srcWidth = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0].PixelWidth;
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                if (srcWidth > decodeWidth) bi.DecodePixelWidth = decodeWidth;     // never upscale a small picture
                bi.UriSource = new Uri(file);
                bi.EndInit();
                bi.Freeze();
                return (BitmapSource)bi;
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }
}
