using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PortablePlayer;

/// <summary>Everything is stored in a "data" folder next to the exe (portable). Falls back to %LOCALAPPDATA% if read-only.</summary>
public static class Paths
{
    public static readonly string AppDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
    public static readonly string DataDir = InitDataDir();

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    static string InitDataDir()
    {
        try
        {
            string d = Path.Combine(AppDir, "data");
            Directory.CreateDirectory(d);
            string probe = Path.Combine(d, ".write-test");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return d;
        }
        catch
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortableMusicPlayer");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    /// <summary>Paths on the same drive as the data folder are stored relative, so the app survives a changed drive letter.</summary>
    public static string ToStored(string p)
    {
        try
        {
            if (!string.IsNullOrEmpty(p) && Path.IsPathRooted(p) &&
                string.Equals(Path.GetPathRoot(p), Path.GetPathRoot(DataDir), StringComparison.OrdinalIgnoreCase))
                return Path.GetRelativePath(DataDir, p);
        }
        catch { }
        return p;
    }

    public static string FromStored(string p)
    {
        try { return Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(DataDir, p)); }
        catch { return p; }
    }

    public static void WriteAtomic(string file, string text)
    {
        string tmp = file + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, file, true);
    }

    public static void Log(object o)
    {
        try { File.AppendAllText(Path.Combine(DataDir, "crash.log"), $"[{DateTime.Now:s}] {o}\n\n"); }
        catch { }
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int index, int value);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);

    public const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    public const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;
}
