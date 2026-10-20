using System;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace PortablePlayer;

/// <summary>Notification-area icon and its menu. All actions go through App / PlaybackCore.</summary>
public sealed class TrayService : IDisposable
{
    readonly WinForms.NotifyIcon _icon;
    readonly WinForms.ToolStripMenuItem _play, _panel;
    readonly App _app = App.Instance;

    public TrayService()
    {
        System.Drawing.Icon ico;
        try
        {
            var sri = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
            ico = new System.Drawing.Icon(sri.Stream);
        }
        catch { ico = System.Drawing.SystemIcons.Application; }

        var menu = new WinForms.ContextMenuStrip();
        var header = new WinForms.ToolStripMenuItem("Vibe") { Enabled = false };
        header.Font = new System.Drawing.Font(header.Font, System.Drawing.FontStyle.Bold);
        _play = new WinForms.ToolStripMenuItem("Play", null, (_, _) => _app.Core.Toggle());
        var prev = new WinForms.ToolStripMenuItem("Previous", null, (_, _) => _app.Core.Previous());
        var next = new WinForms.ToolStripMenuItem("Next", null, (_, _) => _app.Core.Next());
        var show = new WinForms.ToolStripMenuItem("Show Player", null, (_, _) => _app.ShowPlayer());
        _panel = new WinForms.ToolStripMenuItem("Show Desktop Panel", null, (_, _) => _app.TogglePanel());
        var settings = new WinForms.ToolStripMenuItem("Settings", null, (_, _) => _app.ShowSettings());
        var exit = new WinForms.ToolStripMenuItem("Exit", null, (_, _) => _app.ExitApp());

        menu.Items.AddRange(new WinForms.ToolStripItem[]
        {
            header, new WinForms.ToolStripSeparator(),
            _play, prev, next, new WinForms.ToolStripSeparator(),
            show, _panel, settings, new WinForms.ToolStripSeparator(), exit
        });
        menu.Opening += (_, _) => RefreshMenu();

        _icon = new WinForms.NotifyIcon { Icon = ico, Text = "Vibe", ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) _app.ShowPlayer(); };
        _app.Core.Changed += c => { if (c == CoreChange.Track || c == CoreChange.State) UpdateTip(); };
        UpdateTip();
    }

    void RefreshMenu()
    {
        _play.Text = _app.Core.State == PlayState.Playing ? "Pause" : "Play";
        bool panelVisible = _app.Settings.PanelEnabled && !_app.MainVisible;
        _panel.Text = panelVisible ? "Hide Desktop Panel" : "Show Desktop Panel";
    }

    void UpdateTip()
    {
        var t = _app.Core.Current;
        string s = t == null ? "Vibe" : $"{t.Title} – {t.Artist}";
        if (s.Length > 63) s = s.Substring(0, 60) + "...";
        _icon.Text = s;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
