using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Interop;

namespace PortablePlayer;

/// <summary>Global hotkeys via RegisterHotKey on a hidden host window.</summary>
public sealed class HotkeyService : IDisposable
{
    public static readonly (string Key, string Label, string Default)[] Actions =
    {
        ("TogglePanel",     "Show / hide desktop panel", "Ctrl+Alt+D"),
        ("PlayPause",       "Play / pause",              "Ctrl+Alt+Space"),
        ("Previous",        "Previous track",            "Ctrl+Alt+Left"),
        ("Next",            "Next track",                "Ctrl+Alt+Right"),
        ("ToggleMain",      "Show / hide main player",   "Ctrl+Alt+M"),
        ("TogglePanelLock", "Lock / unlock panel",       "Ctrl+Alt+L"),
    };

    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    readonly IntPtr _hwnd;
    readonly HwndSource _src;
    readonly Dictionary<int, string> _ids = new();
    int _next = 1;

    public HotkeyService(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _src = HwndSource.FromHwnd(hwnd);
        _src?.AddHook(Hook);
    }

    IntPtr Hook(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _ids.TryGetValue(wParam.ToInt32(), out var key))
        {
            handled = true;
            try { App.Instance?.RunAction(key); } catch (Exception ex) { Paths.Log(ex); }
        }
        return IntPtr.Zero;
    }

    /// <summary>Re-registers everything. Returns the labels of hotkeys that could not be registered.</summary>
    public List<string> Register(Dictionary<string, string> map)
    {
        UnregisterAll();
        var failed = new List<string>();
        foreach (var a in Actions)
        {
            if (!map.TryGetValue(a.Key, out var text) || string.IsNullOrWhiteSpace(text)) continue;
            if (!TryParse(text, out var mods, out var key)) { failed.Add(a.Label); continue; }
            uint m = 0;
            if (mods.HasFlag(ModifierKeys.Alt)) m |= MOD_ALT;
            if (mods.HasFlag(ModifierKeys.Control)) m |= MOD_CONTROL;
            if (mods.HasFlag(ModifierKeys.Shift)) m |= MOD_SHIFT;
            if (mods.HasFlag(ModifierKeys.Windows)) m |= MOD_WIN;
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            int id = _next++;
            if (Native.RegisterHotKey(_hwnd, id, m | MOD_NOREPEAT, vk)) _ids[id] = a.Key;
            else failed.Add(a.Label);
        }
        return failed;
    }

    void UnregisterAll()
    {
        foreach (var id in _ids.Keys) Native.UnregisterHotKey(_hwnd, id);
        _ids.Clear();
    }

    public static bool TryParse(string text, out ModifierKeys mods, out Key key)
    {
        mods = ModifierKeys.None; key = Key.None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var raw in text.Split('+'))
        {
            string p = raw.Trim();
            switch (p.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "win": mods |= ModifierKeys.Windows; break;
                default:
                    if (!Enum.TryParse(p, true, out key)) return false;
                    break;
            }
        }
        return key != Key.None && mods != ModifierKeys.None;
    }

    public static string Format(ModifierKeys mods, Key key)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    public void Dispose()
    {
        UnregisterAll();
        _src?.RemoveHook(Hook);
    }
}
