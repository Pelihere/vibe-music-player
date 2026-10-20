using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PortablePlayer;

public partial class App : Application
{
    public static App Instance { get; private set; }

    public AppSettings Settings { get; private set; }
    public LibraryStore Library { get; private set; }
    public PlaybackCore Core { get; private set; }
    public MainWindow MainWin { get; private set; }
    public DesktopPanel PanelWin { get; private set; }
    public TrayService Tray { get; private set; }
    public HotkeyService Hotkeys { get; private set; }
    public MediaControls Media { get; private set; }
    public bool Exiting { get; private set; }
    public List<string> HotkeyFailures { get; private set; } = new();

    public bool MainVisible => MainWin != null && !MainWin.IsDisposed && MainWin.IsVisible;

    Mutex _mutex;
    EventWaitHandle _showEvent;
    Window _host;

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "PortableMusicPlayer";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Instance = this;

        // ---- single instance ----
        _mutex = new Mutex(true, @"Local\PortableMusicPlayer.Single", out bool first);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\PortableMusicPlayer.Show");
        if (!first)
        {
            try { _showEvent.Set(); } catch { }
            Shutdown();
            return;
        }
        var listener = new Thread(() =>
        {
            while (!Exiting)
            {
                try { _showEvent.WaitOne(); } catch { return; }
                if (Exiting) return;
                Dispatcher.BeginInvoke(new Action(ShowPlayer));
            }
        }) { IsBackground = true, Name = "SingleInstanceListener" };
        listener.Start();

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Paths.Log(a.ExceptionObject);

        // ---- core ----
        Settings = AppSettings.Load();
        ThemeManager.Init(Settings.Theme);
        Library = LibraryStore.Load();
        Core = new PlaybackCore(Settings, Library);

        // hidden host window: owns hotkeys and system media controls
        _host = new Window
        {
            Width = 0, Height = 0, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, Left = -32000, Top = -32000, Visibility = Visibility.Hidden
        };
        IntPtr hwnd = new WindowInteropHelper(_host).EnsureHandle();
        Hotkeys = new HotkeyService(hwnd);
        HotkeyFailures = Hotkeys.Register(Settings.Hotkeys);
        Media = new MediaControls(hwnd, Core);
        Tray = new TrayService();

        Core.Restore();
        System.Threading.Tasks.Task.Run(ArtistImageService.CleanupLegacyFiles);
        Core.Changed += c => { if (c == CoreChange.Track) TrackMetadata.FlushPending(); };
        TrackMetadata.FlushPending();
        SetAutostart(Settings.StartWithWindows);

        bool background = e.Args.Any(a => string.Equals(a, "--background", StringComparison.OrdinalIgnoreCase));
        if (background && !Settings.OpenPlayerOnWindowsStart)
        {
            EnsureMain();      // created hidden, so the queue/library are ready
            UpdatePanel();
        }
        else ShowPlayer();
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Paths.Log(e.Exception);
        e.Handled = true;
        try
        {
            if (Core == null) return;          // failed during startup: nothing to recover, avoid cascading errors
            if (Core.State == PlayState.Error) Core.Recover();
            EnsureMain();
            UpdatePanel();
        }
        catch (Exception ex) { Paths.Log(ex); }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        Exiting = true;
        try { Core?.Shutdown(); Settings?.Save(); Library?.Save(); } catch { }
        base.OnSessionEnding(e);
    }

    // ------------------------------------------------------------------ windows
    public void EnsureMain()
    {
        if (MainWin == null || MainWin.IsDisposed) MainWin = new MainWindow();
    }

    public void ShowPlayer()
    {
        if (Exiting) return;
        EnsureMain();
        if (MainWin.WindowState == WindowState.Minimized) MainWin.WindowState = WindowState.Normal;
        MainWin.Show();
        MainWin.Activate();
        UpdatePanel();
    }

    public void HideMainToTray()
    {
        if (MainWin != null && !MainWin.IsDisposed) MainWin.Hide();
        UpdatePanel();
    }

    /// <summary>The panel is visible only while enabled AND the main player is hidden.</summary>
    public void UpdatePanel()
    {
        if (Exiting) return;
        bool want = Settings.PanelEnabled && !MainVisible;
        if (want)
        {
            PanelWin ??= new DesktopPanel();
            PanelWin.ApplyAppearance();
            PanelWin.ShowPanel();
        }
        else PanelWin?.HidePanel();
    }

    public void SetPanelEnabled(bool on)
    {
        Settings.PanelEnabled = on;
        Settings.Save();
        if (on && MainVisible) MainWin.Hide();
        UpdatePanel();
    }

    public void TogglePanel() => SetPanelEnabled(!(Settings.PanelEnabled && !MainVisible));

    /// <summary>Settings are a page of the main window (same shell, theme and mini player), not a separate window.</summary>
    public void ShowSettings()
    {
        ShowPlayer();
        MainWin?.Navigate("Settings");
    }

    /// <summary>Called by the settings page after it changed something.</summary>
    public List<string> SettingsChanged(bool hotkeys)
    {
        Settings.Save();
        if (hotkeys) HotkeyFailures = Hotkeys.Register(Settings.Hotkeys);
        SetAutostart(Settings.StartWithWindows);
        PanelWin?.ApplyAppearance();
        UpdatePanel();
        return HotkeyFailures;
    }

    public void RunAction(string key)
    {
        switch (key)
        {
            case "TogglePanel": TogglePanel(); break;
            case "PlayPause": Core.Toggle(); break;
            case "Previous": Core.Previous(); break;
            case "Next": Core.Next(); break;
            case "ToggleMain":
                if (MainVisible && MainWin.WindowState != WindowState.Minimized) HideMainToTray(); else ShowPlayer();
                break;
            case "TogglePanelLock":
                Settings.Locked = !Settings.Locked;
                Settings.Save();
                PanelWin?.ApplyAppearance();
                break;
        }
    }

    public void SetTheme(string name, bool animate = true)
    {
        Settings.Theme = ThemeManager.Find(name).Name;
        ThemeManager.Apply(Settings.Theme, animate);
        Settings.Save();
    }

    public void SetAutostart(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (on)
            {
                string exe = Environment.ProcessPath ?? "";
                string cmd = $"\"{exe}\"" + (Settings.OpenPlayerOnWindowsStart ? "" : " --background");
                k.SetValue(RunName, cmd);
            }
            else k.DeleteValue(RunName, false);
        }
        catch (Exception ex) { Paths.Log(ex); }
    }

    public void ExitApp()
    {
        if (Exiting) return;
        Exiting = true;
        try { _showEvent.Set(); } catch { }
        try { Core?.Shutdown(); } catch (Exception ex) { Paths.Log(ex); }
        try { Settings?.Save(); Library?.Save(); } catch { }
        try { Hotkeys?.Dispose(); } catch { }
        try { Media?.Dispose(); } catch { }
        try { Tray?.Dispose(); } catch { }
        try { PanelWin?.Close(); } catch { }
        try { MainWin?.Close(); } catch { }
        try { _mutex?.ReleaseMutex(); } catch { }
        Shutdown();
    }
}
