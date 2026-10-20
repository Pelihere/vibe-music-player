using System.Windows;
using System.Windows.Media;

namespace PortablePlayer;

/// <summary>Small attached state used by the row styles to draw the drag-and-drop insertion line.</summary>
public static class Dnd
{
    public static readonly DependencyProperty IsTargetProperty =
        DependencyProperty.RegisterAttached("IsTarget", typeof(bool), typeof(Dnd), new PropertyMetadata(false));

    public static bool GetIsTarget(DependencyObject d) => (bool)d.GetValue(IsTargetProperty);
    public static void SetIsTarget(DependencyObject d, bool v) => d.SetValue(IsTargetProperty, v);
}

public static class UiUtil
{
    public static T FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
}
