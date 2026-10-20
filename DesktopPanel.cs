using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace PortablePlayer;

/// <summary>
/// Independent floating panel. It owns no audio: it renders PlaybackCore state and forwards clicks to it.
/// Shown only while the main player is hidden (App.UpdatePanel decides).
/// </summary>
public partial class DesktopPanel : Window
{
    const double HaloMargin = 14;                 // transparent halo margin around the card (DIPs)

    static Brush Frozen(Brush b) { b.Freeze(); return b; }

    readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    PanelDock? _laidDock;
    bool _wired, _dragging, _placing, _menuOpen, _concealed;
    int _fadeToken;
    DateTime _lastActivity = DateTime.UtcNow;

    static App A => App.Instance;
    static AppSettings S => App.Instance.Settings;
    static PlaybackCore C => App.Instance.Core;

    public DesktopPanel()
    {
        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.SetWindowLong(h, Native.GWL_EXSTYLE, Native.GetWindowLong(h, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW);
        };

        Info.QueueClicked += () => { A.ShowPlayer(); A.MainWin?.ShowQueue(); };

        // context menu
        MiShow.Click += (_, _) => A.ShowPlayer();
        MiLock.Click += (_, _) => { S.Locked = MiLock.IsChecked; S.Save(); };
        MiHide.Click += (_, _) => A.SetPanelEnabled(false);
        Menu.Opened += (_, _) => { _menuOpen = true; MiLock.IsChecked = S.Locked; };
        Menu.Closed += (_, _) => { _menuOpen = false; _lastActivity = DateTime.UtcNow; };

        MouseLeftButtonDown += OnDown;
        MouseWheel += (_, e) => C.SetVolume(C.Volume + Math.Sign(e.Delta) * 0.05);
        _hideTimer.Tick += (_, _) => AutoHideTick();

        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        Closed += (_, _) =>
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            _hideTimer.Stop();
            if (_wired) C.Changed -= OnCore;
        };
    }

    // ------------------------------------------------------------------ show / hide
    public void ShowPanel()
    {
        if (!_wired) { C.Changed += OnCore; _wired = true; }
        ApplyColors();
        _lastActivity = DateTime.UtcNow;
        _concealed = false;

        if (!IsVisible)
        {
            Opacity = 0;
            Left = -32000; Top = -32000;
            Show();
            Place();
        }
        else Place();
        FadeTo(1, null);
        UpdateTimer();
    }

    public void HidePanel()
    {
        if (!IsVisible) return;
        _hideTimer.Stop();
        if (_wired) { C.Changed -= OnCore; _wired = false; }
        FadeTo(0, () => Hide());
    }

    int FadeMs => S.Animations switch { AnimMode.Full => 220, AnimMode.Reduced => 110, _ => 0 };

    void FadeTo(double target, Action done)
    {
        int token = ++_fadeToken;
        BeginAnimation(OpacityProperty, null);
        int ms = FadeMs;
        if (ms == 0 || Math.Abs(Opacity - target) < 0.01)
        {
            Opacity = target;
            done?.Invoke();
            return;
        }
        var a = new DoubleAnimation(Opacity, target, TimeSpan.FromMilliseconds(ms));
        a.Completed += (_, _) =>
        {
            if (token != _fadeToken) return;
            BeginAnimation(OpacityProperty, null);
            Opacity = target;
            done?.Invoke();
        };
        BeginAnimation(OpacityProperty, a);
    }

    // ------------------------------------------------------------------ appearance
    public void ApplyAppearance()
    {
        bool horiz = S.Dock == PanelDock.Top;

        if (_laidDock != null && _laidDock != S.Dock) { S.PanelX = null; S.PanelY = null; }   // new dock = re-dock
        _laidDock = S.Dock;

        // design size of the card, then the Viewbox scales it to the window
        double cw = horiz ? 660 : 300, ch = horiz ? 240 : 470;
        Card.Width = cw; Card.Height = ch;
        if (horiz)
        {
            R0.Height = new GridLength(1, GridUnitType.Star); R1.Height = new GridLength(0);
            C0.Width = new GridLength(270); C1.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(Gramo, 0); Grid.SetColumn(Gramo, 0);
            Grid.SetRow(Info, 0); Grid.SetColumn(Info, 1);
            Info.Margin = new Thickness(6, 0, 22, 0);
        }
        else
        {
            R0.Height = new GridLength(262); R1.Height = new GridLength(1, GridUnitType.Star);
            C0.Width = new GridLength(1, GridUnitType.Star); C1.Width = new GridLength(0);
            Grid.SetRow(Gramo, 0); Grid.SetColumn(Gramo, 0);
            Grid.SetRow(Info, 1); Grid.SetColumn(Info, 0);
            Info.Margin = new Thickness(18, 0, 18, 10);
        }

        Topmost = S.AlwaysOnTop;
        MiLock.IsChecked = S.Locked;

        ApplyColors();
        UpdateTimer();
        if (IsVisible) Place();
    }

    void ApplyColors()
    {
        var pal = S.DynamicTheme ? C.Palette : Palette.Default;
        byte a = (byte)Math.Round(255 * S.Transparency);
        var g = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(1, 1) };
        g.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(a, pal.Primary.R, pal.Primary.G, pal.Primary.B), 0));
        g.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(a, pal.Secondary.R, pal.Secondary.G, pal.Secondary.B), 1));
        g.Freeze();
        Card.Background = g;

        var acc = pal.Accent;
        Halo.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(0x70 * S.Transparency), acc.R, acc.G, acc.B));
        Halo.Opacity = S.Transparency;

        Info.Accent = Frozen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(acc.R, acc.G, acc.B)));
    }

    // ------------------------------------------------------------------ core → UI
    // Gramophone + deck drive themselves; the panel only recolors its card when the artwork palette changes.
    void OnCore(CoreChange c)
    {
        if (c == CoreChange.Track && IsVisible) ApplyColors();
    }

    // ------------------------------------------------------------------ dragging
    void OnDown(object sender, MouseButtonEventArgs e)
    {
        _lastActivity = DateTime.UtcNow;
        if (S.Locked || e.ButtonState != MouseButtonState.Pressed) return;
        _dragging = true;
        try { DragMove(); }
        catch (InvalidOperationException) { }
        finally { _dragging = false; }

        if (Native.GetWindowRect(new WindowInteropHelper(this).Handle, out var r))
        {
            S.PanelX = r.Left; S.PanelY = r.Top;
            Place();                       // clamp to the monitor it ended up on
            S.SaveSoon();
        }
    }

    // ------------------------------------------------------------------ placement (pixels)
    (int w, int h) SizePx()
    {
        bool horiz = S.Dock == PanelDock.Top;
        double f = S.Size switch { PanelSize.Compact => 0.8, PanelSize.Large => 1.25, _ => 1.0 };
        double w = (horiz ? 660 : 300) * f + HaloMargin * 2;
        double h = (horiz ? 240 : 470) * f + HaloMargin * 2;
        var dpi = VisualTreeHelper.GetDpi(this);
        return ((int)Math.Round(w * dpi.DpiScaleX), (int)Math.Round(h * dpi.DpiScaleY));
    }

    /// <summary>Positions the window in physical pixels, always keeping it on a connected monitor.</summary>
    void Place()
    {
        if (_placing || _dragging) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        _placing = true;
        try
        {
            var (w, h) = SizePx();
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            int m = (int)Math.Round(HaloMargin * scale);
            int gap = (int)Math.Round(10 * scale);
            System.Drawing.Rectangle wa;
            int x, y;

            if (S.PanelX is int px && S.PanelY is int py)
            {
                wa = WinForms.Screen.FromRectangle(new System.Drawing.Rectangle(px, py, w, h)).WorkingArea;
                x = px; y = py;
            }
            else
            {
                wa = WinForms.Screen.PrimaryScreen.WorkingArea;
                switch (S.Dock)
                {
                    case PanelDock.Left: x = wa.Left + gap - m; y = wa.Top + (wa.Height - h) / 2; break;
                    case PanelDock.Top: x = wa.Left + (wa.Width - w) / 2; y = wa.Top + gap - m; break;
                    default: x = wa.Right - w - gap + m; y = wa.Top + (wa.Height - h) / 2; break;
                }
            }

            // the halo margin may hang over the edge, the card itself never leaves the work area
            int minX = wa.Left - m, maxX = wa.Right - w + m;
            int minY = wa.Top - m, maxY = wa.Bottom - h + m;
            x = maxX < minX ? minX : Math.Clamp(x, minX, maxX);
            y = maxY < minY ? minY : Math.Clamp(y, minY, maxY);

            Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
        finally { _placing = false; }
    }

    void OnDisplayChanged(object sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() => { if (IsVisible) Place(); }));

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (!_dragging) Dispatcher.BeginInvoke(new Action(() => { if (IsVisible) Place(); }), DispatcherPriority.Background);
    }

    // ------------------------------------------------------------------ auto-hide
    void UpdateTimer()
    {
        if (S.AutoHide && IsVisible) _hideTimer.Start();
        else
        {
            _hideTimer.Stop();
            if (_concealed) { _concealed = false; FadeTo(1, null); }
        }
    }

    void AutoHideTick()
    {
        if (!S.AutoHide || !IsVisible) { UpdateTimer(); return; }
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!Native.GetCursorPos(out var p) || !Native.GetWindowRect(hwnd, out var r)) return;

        bool inside = p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
        if (!inside)
        {
            // thin zone at the screen edge the panel is docked to brings it back
            var scr = WinForms.Screen.FromHandle(hwnd).Bounds;
            int edge = (int)Math.Round(6 * VisualTreeHelper.GetDpi(this).DpiScaleX);
            int near = (int)Math.Round(80 * VisualTreeHelper.GetDpi(this).DpiScaleX);
            inside = S.Dock switch
            {
                PanelDock.Left => p.X <= scr.Left + edge && p.Y >= r.Top && p.Y < r.Bottom && r.Left - scr.Left < near,
                PanelDock.Right => p.X >= scr.Right - edge - 1 && p.Y >= r.Top && p.Y < r.Bottom && scr.Right - r.Right < near,
                _ => p.Y <= scr.Top + edge && p.X >= r.Left && p.X < r.Right && r.Top - scr.Top < near
            };
        }

        if (inside || _dragging || _menuOpen || Mouse.LeftButton == MouseButtonState.Pressed)
        {
            _lastActivity = DateTime.UtcNow;
            if (_concealed) { _concealed = false; FadeTo(1, null); }
        }
        else if (!_concealed && (DateTime.UtcNow - _lastActivity).TotalSeconds >= S.AutoHideSeconds)
        {
            _concealed = true;
            FadeTo(0, null);
        }
    }
}
