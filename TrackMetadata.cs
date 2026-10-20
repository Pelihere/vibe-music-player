using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace PortablePlayer;

/// <summary>Values shown in the metadata editor.</summary>
public sealed class TrackEdit
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string AlbumArtist { get; set; } = "";
    public string Genre { get; set; } = "";
    public int TrackNo { get; set; }
    public int Disc { get; set; }
    public int Year { get; set; }
    /// <summary>Image file picked by the user (null = keep the current cover).</summary>
    public string NewCoverFile { get; set; }
    public bool RemoveCover { get; set; }

    public static TrackEdit From(Track t) => new()
    {
        Title = t.Title, Artist = t.Artist, Album = t.Album, AlbumArtist = t.AlbumArtist, Genre = t.Genre,
        TrackNo = t.No, Disc = t.Disc, Year = t.Year
    };
}

public enum MetaSaveResult
{
    /// <summary>Saved in Vibe and written into the audio file.</summary>
    FileAndLibrary,
    /// <summary>Saved in Vibe; the file is busy (playing) and will be written as soon as it is free.</summary>
    LibraryFilePending,
    /// <summary>Saved in Vibe only: this file cannot be written (format / permissions) or writing is switched off.</summary>
    LibraryOnly
}

/// <summary>Writes tags into audio files. The UI and the library never talk to a file format directly.</summary>
public interface IMetadataWriter
{
    /// <summary>True when the file was written; throws IOException / UnauthorizedAccessException when it is locked or read-only.</summary>
    bool Write(string path, TrackEdit e, string coverFile, bool removeCover);
}

public sealed class TagLibWriter : IMetadataWriter
{
    public bool Write(string path, TrackEdit e, string coverFile, bool removeCover)
    {
        TagLib.File f;
        try { f = TagLib.File.Create(path); }
        catch (Exception ex) when (ex is TagLib.UnsupportedFormatException or TagLib.CorruptFileException) { return false; }

        using (f)
        {
            if (f.Writeable == false) return false;
            var tag = f.Tag;
            tag.Title = e.Title;
            tag.Performers = string.IsNullOrWhiteSpace(e.Artist) ? Array.Empty<string>() : new[] { e.Artist };
            tag.AlbumArtists = string.IsNullOrWhiteSpace(e.AlbumArtist) ? Array.Empty<string>() : new[] { e.AlbumArtist };
            tag.Album = e.Album;
            tag.Genres = string.IsNullOrWhiteSpace(e.Genre) ? Array.Empty<string>() : new[] { e.Genre };
            tag.Year = (uint)Math.Max(0, e.Year);
            tag.Track = (uint)Math.Max(0, e.TrackNo);
            tag.Disc = (uint)Math.Max(0, e.Disc);
            if (coverFile != null && File.Exists(coverFile)) tag.Pictures = new TagLib.IPicture[] { new TagLib.Picture(coverFile) };
            else if (removeCover) tag.Pictures = Array.Empty<TagLib.IPicture>();
            try { f.Save(); return true; }
            catch (Exception ex) when (ex is TagLib.UnsupportedFormatException or TagLib.CorruptFileException or NotSupportedException) { return false; }
        }
    }
}

public static class TrackMetadata
{
    static readonly IMetadataWriter Writer = new TagLibWriter();
    static string CoverDir => Directory.CreateDirectory(Path.Combine(Paths.DataDir, "covers")).FullName;

    static string Hash(string s)
    {
        using var sha = SHA1.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s.ToLowerInvariant()))).Substring(0, 16).ToLowerInvariant();
    }

    /// <summary>Re-encodes the picked image as PNG (max 1200 px) into the data folder, so any common image format works.</summary>
    static string StoreCover(Track t, string source)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.DecodePixelWidth = 1200;
        bi.UriSource = new Uri(source);
        bi.EndInit();
        bi.Freeze();
        string dest = Path.Combine(CoverDir, Hash(t.FilePath) + "-" + DateTime.UtcNow.Ticks % 100000 + ".png");
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bi));
        using (var fs = File.Create(dest)) enc.Save(fs);
        return dest;
    }

    /// <summary>
    /// Applies an edit: library values change first (so the UI updates at once and nothing is lost), then the file is written
    /// when possible. The track keeps its identity, so no duplicate is ever created.
    /// </summary>
    public static async Task<MetaSaveResult> ApplyAsync(Track t, TrackEdit e)
    {
        var app = App.Instance;
        string oldCover = t.CoverFile;

        // 1. library layer (always)
        t.Title = Cmp.Norm(e.Title);
        t.Artist = string.IsNullOrWhiteSpace(e.Artist) ? "Unknown Artist" : Cmp.Norm(e.Artist);
        t.Album = string.IsNullOrWhiteSpace(e.Album) ? "Unknown Album" : Cmp.Norm(e.Album);
        t.AlbumArtist = Cmp.Norm(e.AlbumArtist);
        t.Genre = Cmp.Norm(e.Genre);
        t.No = Math.Max(0, e.TrackNo);
        t.Disc = Math.Max(0, e.Disc);
        t.Year = Math.Max(0, e.Year);
        t.Edited = true;

        string coverForFile = null;
        bool coverChanged = false;
        if (e.NewCoverFile != null)
        {
            coverForFile = await Task.Run(() => StoreCover(t, e.NewCoverFile));
            t.Cover = Paths.ToStored(coverForFile);
            coverChanged = true;
        }
        else if (e.RemoveCover && !string.IsNullOrEmpty(t.Cover))
        {
            t.Cover = "";
            coverChanged = true;
        }
        if (coverChanged && oldCover != null && !string.Equals(oldCover, coverForFile, StringComparison.OrdinalIgnoreCase))
            try { File.Delete(oldCover); } catch { }

        // 2. audio file (when allowed and possible)
        var result = MetaSaveResult.LibraryOnly;
        if (app.Settings.WriteTagsToFiles)
        {
            var snapshot = TrackEdit.From(t);
            string path = t.FilePath;
            bool remove = e.RemoveCover;
            try
            {
                bool ok = await Task.Run(() => Writer.Write(path, snapshot, coverForFile, remove));
                t.PendingWrite = false;
                result = ok ? MetaSaveResult.FileAndLibrary : MetaSaveResult.LibraryOnly;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                t.PendingWrite = File.Exists(path);                       // busy → retry later
                result = t.PendingWrite ? MetaSaveResult.LibraryFilePending : MetaSaveResult.LibraryOnly;
            }
            catch (Exception ex) { Paths.Log(ex); result = MetaSaveResult.LibraryOnly; }
        }
        else t.PendingWrite = false;

        // 3. everything that shows this track
        ArtworkService.Invalidate();
        ThumbService.Invalidate();
        t.ResetThumb();
        app.Library.Touch(t);
        if (ReferenceEquals(app.Core.Current, t)) app.Core.RefreshCurrent();
        return result;
    }

    /// <summary>Writes edits that had to wait because the file was in use (never touches the track that is playing now).</summary>
    public static async void FlushPending()
    {
        var app = App.Instance;
        if (app?.Library == null || !app.Settings.WriteTagsToFiles) return;
        var cur = app.Core?.Current;
        var todo = app.Library.Tracks.Where(t => t.PendingWrite && !ReferenceEquals(t, cur)).ToList();
        foreach (var t in todo)
        {
            var snap = TrackEdit.From(t);
            string path = t.FilePath, cover = t.CoverFile;
            try
            {
                await Task.Run(() => Writer.Write(path, snap, cover != null && File.Exists(cover) ? cover : null, false));
                t.PendingWrite = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* still busy: try next time */ }
            catch (Exception ex) { Paths.Log(ex); t.PendingWrite = false; }
        }
        if (todo.Count > 0) app.Library.SaveSoon();
    }
}
