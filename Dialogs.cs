using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace PortablePlayer;

public enum DialogKind { Info, Question, Warning, Danger, Success }

/// <summary>
/// Themed in-app dialogs (confirm / info / text input) that replace the plain system message boxes.
/// They follow the Main Window theme: accent line, glyph badge, title, message, optional fact list, clear buttons.
/// </summary>
public static class Dialogs
{
    /// <summary>OK / Cancel question. Returns true when the user confirms.</summary>
    public static bool Confirm(Window owner, string title, string message, string okText = "OK", DialogKind kind = DialogKind.Question,
                               IEnumerable<(string Label, string Value)> facts = null, string cancelText = "Cancel")
        => Show(owner, title, message, kind, okText, cancelText, facts, null, null, out _);

    /// <summary>A message with one button.</summary>
    public static void Info(Window owner, string title, string message, DialogKind kind = DialogKind.Info,
                            IEnumerable<(string Label, string Value)> facts = null, string okText = "Got it")
        => Show(owner, title, message, kind, okText, null, facts, null, null, out _);

    /// <summary>Single-line text input. Returns null when cancelled.</summary>
    public static string Ask(Window owner, string title, string message, string initial = "", string hint = null, string okText = "OK")
    {
        bool ok = Show(owner, title, message, DialogKind.Question, okText, "Cancel", null, initial ?? "", hint, out string text);
        return ok ? text : null;
    }

    static (string Glyph, string Key, Color? Fixed) Look(DialogKind k) => k switch
    {
        DialogKind.Danger => ("✕", null, Color.FromRgb(0xE5, 0x48, 0x4D)),
        DialogKind.Warning => ("!", null, Color.FromRgb(0xF5, 0xA5, 0x24)),
        DialogKind.Success => ("✓", null, Color.FromRgb(0x2D, 0xD4, 0x8F)),
        DialogKind.Info => ("i", "Accent", null),
        _ => ("?", "Accent", null)
    };

    static TextBlock Text(string text, double size, string key, FontWeight? w = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, key);
        if (w != null) t.FontWeight = w.Value;
        return t;
    }

    static bool Show(Window owner, string title, string message, DialogKind kind, string okText, string cancelText,
                     IEnumerable<(string Label, string Value)> facts, string input, string hint, out string text)
    {
        var (glyph, key, fixedColor) = Look(kind);
        Brush tone = fixedColor != null ? new SolidColorBrush(fixedColor.Value) : null;
        bool result = false;
        string typed = null;

        // ---- card
        var card = new Border { BorderThickness = new Thickness(2), RenderTransformOrigin = new Point(0.5, 0.5) };
        card.SetResourceReference(Border.BorderThicknessProperty, "BorderThin");
        card.SetResourceReference(Border.BackgroundProperty, "Surface2");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusLg");
        card.SetResourceReference(UIElement.EffectProperty, "CardShadow");
        var scale = new ScaleTransform(1, 1);
        card.RenderTransform = scale;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var line = new Border { Height = 3 };
        if (tone != null) line.Background = tone; else line.SetResourceReference(Border.BackgroundProperty, "AccentGradient");
        root.Children.Add(line);

        var body = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        // ---- header: badge + title
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var badge = new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(21), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top, Opacity = 1 };
        var badgeFill = new Border { CornerRadius = new CornerRadius(21), Opacity = 0.18 };
        var ring = new Border { CornerRadius = new CornerRadius(21), BorderThickness = new Thickness(1.5) };
        var gl = new TextBlock { Text = glyph, FontSize = 19, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (tone != null) { badgeFill.Background = tone; ring.BorderBrush = tone; gl.Foreground = tone; }
        else
        {
            badgeFill.SetResourceReference(Border.BackgroundProperty, key);
            ring.SetResourceReference(Border.BorderBrushProperty, key);
            gl.SetResourceReference(TextBlock.ForegroundProperty, key);
        }
        var badgeGrid = new Grid();
        badgeGrid.Children.Add(badgeFill); badgeGrid.Children.Add(ring); badgeGrid.Children.Add(gl);
        badge.Child = badgeGrid;
        head.Children.Add(badge);

        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Text(title, 18, "Fg", FontWeights.SemiBold));
        Grid.SetColumn(titles, 1);
        head.Children.Add(titles);
        body.Children.Add(head);

        if (!string.IsNullOrWhiteSpace(message))
        {
            var m = Text(message, 13.5, "Muted");
            m.Margin = new Thickness(56, 8, 0, 0);
            m.LineHeight = 20;
            body.Children.Add(m);
        }

        // ---- facts (small inset table)
        var fl = facts?.Where(f => !string.IsNullOrEmpty(f.Value)).ToList();
        if (fl is { Count: > 0 })
        {
            var box = new Border { Margin = new Thickness(56, 14, 0, 0), Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(1) };
            box.SetResourceReference(Border.BackgroundProperty, "Surface");
            box.SetResourceReference(Border.BorderBrushProperty, "Border");
            box.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < fl.Count; i++)
            {
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var a = Text(fl[i].Label, 12, "Muted"); a.Margin = new Thickness(0, 3, 16, 3);
                var b = Text(fl[i].Value, 12.5, "Fg"); b.Margin = new Thickness(0, 3, 0, 3);
                Grid.SetRow(a, i); Grid.SetRow(b, i); Grid.SetColumn(b, 1);
                g.Children.Add(a); g.Children.Add(b);
            }
            box.Child = g;
            body.Children.Add(box);
        }

        // ---- text input
        TextBox tb = null;
        if (input != null)
        {
            tb = new TextBox { Style = (Style)owner.FindResource("FieldBox"), Text = input, Margin = new Thickness(56, 14, 0, 0), Height = 36, FontSize = 14 };
            body.Children.Add(tb);
            if (!string.IsNullOrWhiteSpace(hint))
            {
                var h = Text(hint, 11.5, "Muted"); h.Margin = new Thickness(58, 6, 0, 0);
                body.Children.Add(h);
            }
        }

        // ---- buttons
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        Button cancel = null;
        if (cancelText != null)
        {
            cancel = new Button { Content = cancelText, MinWidth = 92, Height = 36, IsCancel = true, Margin = new Thickness(0, 0, 10, 0), Style = (Style)owner.FindResource("TGhost") };
            buttons.Children.Add(cancel);
        }
        var ok = new Button { Content = okText, MinWidth = 104, Height = 36, IsDefault = true, Style = (Style)owner.FindResource("TPrimary") };
        if (kind == DialogKind.Danger) ok.Background = tone;
        buttons.Children.Add(ok);
        body.Children.Add(buttons);

        card.Child = root;
        var shell = new Grid { Margin = new Thickness(14, 14, 22, 22) };       // room for the hard shadow
        shell.Children.Add(card);

        var w = new Window
        {
            Content = shell, Width = 480, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, Title = title
        };
        ok.Click += (_, _) => { result = true; typed = tb?.Text; w.Close(); };
        if (cancel != null) cancel.Click += (_, _) => { result = false; w.Close(); };
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) { result = false; w.Close(); } };
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is TextBox || e.OriginalSource is Button) return;
            try { w.DragMove(); } catch { }
        };
        w.Loaded += (_, _) =>
        {
            (tb as UIElement ?? ok).Focus();
            if (tb != null) tb.SelectAll();
            bool anim = true;
            try { anim = App.Instance.Settings.Animations != AnimMode.Off; } catch { }
            if (!anim) return;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            w.Opacity = 0;
            w.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            scale.ScaleX = scale.ScaleY = 0.95;
            var s = new DoubleAnimation(0.95, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, s);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, s);
        };
        w.ShowDialog();
        text = typed;
        return result;
    }
}
