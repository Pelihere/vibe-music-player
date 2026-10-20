using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace PortablePlayer;

/// <summary>
/// Small cover thumbnails for list rows. One decode per album, at most two at a time on background threads,
/// delivered to the tracks on the UI thread. Rows that are never scrolled into view never cost anything.
/// </summary>
public static class ThumbService
{
    static readonly Dictionary<string, BitmapSource> Cache = new();          // null value = "no cover"
    static readonly Dictionary<string, List<Track>> Pending = new();
    static readonly SemaphoreSlim Gate = new(2);
    static readonly string[] FolderNames = { "cover", "folder", "front", "album", "artwork" };
    static readonly string[] FolderExt = { ".jpg", ".jpeg", ".png" };

    static string Key(Track t)
    {
        if (!string.IsNullOrEmpty(t.Cover)) return "cover|" + t.FilePath;
        bool unknown = string.IsNullOrEmpty(t.Album) || t.Album == "Unknown Album";
        return unknown ? t.FilePath : (Path.GetDirectoryName(t.FilePath) ?? "") + "|" + Cmp.Key(t.Album);
    }

    /// <summary>Forget cached thumbnails (after a cover or album name changed).</summary>
    public static void Invalidate() { Cache.Clear(); }

    public static void Request(Track t)
    {
        string key = Key(t);
        if (Cache.TryGetValue(key, out var hit)) { if (hit != null) t.Thumb = hit; return; }
        if (Pending.TryGetValue(key, out var list)) { list.Add(t); return; }
        Pending[key] = new List<Track> { t };
        string path = t.FilePath;
        string custom = t.CoverFile;

        Task.Run(async () =>
        {
            BitmapSource bmp = null;
            await Gate.WaitAsync();
            try { bmp = Load(path, custom); } catch { }
            finally { Gate.Release(); }
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                Cache[key] = bmp;
                if (Pending.Remove(key, out var waiting) && bmp != null)
                    foreach (var x in waiting) x.Thumb = bmp;
            }));
        });
    }

    static BitmapSource Load(string path, string custom)
    {
        if (custom != null && File.Exists(custom))
        {
            try { return Decode(bi => bi.UriSource = new Uri(custom)); } catch { }
        }
        try
        {
            using var f = TagLib.File.Create(path);
            var pic = f.Tag.Pictures.FirstOrDefault();
            if (pic != null)
            {
                using var ms = new MemoryStream(pic.Data.Data);
                return Decode(bi => bi.StreamSource = ms);
            }
        }
        catch { }
        string dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        foreach (var n in FolderNames)
            foreach (var e in FolderExt)
            {
                string p = Path.Combine(dir, n + e);
                if (File.Exists(p)) return Decode(bi => bi.UriSource = new Uri(p));
            }
        return null;
    }

    static BitmapSource Decode(Action<BitmapImage> source)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.DecodePixelWidth = 96;
        source(bi);
        bi.EndInit();
        bi.Freeze();
        return bi;
    }
}
