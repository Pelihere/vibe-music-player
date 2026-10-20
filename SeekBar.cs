using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PortablePlayer;

/// <summary>Thin progress/volume bar: click or drag to set a value between 0 and 1.</summary>
public sealed class SeekBar : Grid
{
    readonly Border _track = new()
    {
        Height = 4, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255))
    };
    readonly Border _fill = new()
    {
        Height = 4, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Left, Width = 0
    };
    readonly Ellipse _thumb = new()
    {
        Width = 10, Height = 10, Fill = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Left, Opacity = 0, IsHitTestVisible = false
    };
    double _v;
    bool _drag;

    /// <summary>Raised continuously while dragging.</summary>
    public event Action<double> Dragging;
    /// <summary>Raised when the user releases the bar (or clicks).</summary>
    public event Action<double> Commit;

    public SeekBar()
    {
        Background = Brushes.Transparent;
        Height = 18;
        Cursor = Cursors.Hand;
        Children.Add(_track);
        Children.Add(_fill);
        Children.Add(_thumb);
        SizeChanged += (_, _) => Relayout();
    }

    public Brush Fill { get => _fill.Background; set => _fill.Background = value; }
    /// <summary>Background of the empty part of the bar (the Main Window passes a themed brush).</summary>
    public Brush TrackBrush { get => _track.Background; set => _track.Background = value; }
    public Brush ThumbBrush { get => _thumb.Fill; set => _thumb.Fill = value; }

    /// <summary>Binds the bar colours to theme resource keys so they follow theme changes.</summary>
    public void BindTheme(string fillKey, string trackKey, string thumbKey)
    {
        _fill.SetResourceReference(Border.BackgroundProperty, fillKey);
        _track.SetResourceReference(Border.BackgroundProperty, trackKey);
        _thumb.SetResourceReference(Shape.FillProperty, thumbKey);
    }

    /// <summary>Ignored while the user is dragging.</summary>
    public double Value
    {
        get => _v;
        set
        {
            if (_drag) return;
            _v = Math.Clamp(value, 0, 1);
            Relayout();
        }
    }

    void Relayout()
    {
        double w = ActualWidth * _v;
        _fill.Width = w;
        _thumb.Margin = new Thickness(Math.Max(0, w - 5), 0, 0, 0);
    }

    void SetFrom(MouseEventArgs e)
    {
        _v = Math.Clamp(e.GetPosition(this).X / Math.Max(1, ActualWidth), 0, 1);
        Relayout();
        Dragging?.Invoke(_v);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _drag = true;
        CaptureMouse();
        SetFrom(e);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag) SetFrom(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_drag) return;
        _drag = false;
        ReleaseMouseCapture();
        Commit?.Invoke(_v);
        e.Handled = true;
    }

    protected override void OnMouseEnter(MouseEventArgs e) => _thumb.Opacity = 1;
    protected override void OnMouseLeave(MouseEventArgs e) { if (!_drag) _thumb.Opacity = 0; }
}
