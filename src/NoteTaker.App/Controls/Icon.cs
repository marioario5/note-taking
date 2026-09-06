using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NoteTaker.App.Controls;

/// <summary>
/// Renders one Lucide glyph from <c>Theme/Icons.xaml</c>. Wrapping them in a single control
/// means swapping the icon set later is one file, and every glyph inherits its colour from
/// <see cref="Control.Foreground"/> the way the design system expects.
/// </summary>
public sealed class Icon : Control
{
    public static readonly DependencyProperty GeometryProperty = DependencyProperty.Register(
        nameof(Geometry), typeof(Geometry), typeof(Icon), new PropertyMetadata(null));

    /// <summary>Stroke width in the glyph's own 24×24 space, so it scales with the icon.</summary>
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(Icon), new PropertyMetadata(2.0));

    public static readonly DependencyProperty IsFilledProperty = DependencyProperty.Register(
        nameof(IsFilled), typeof(bool), typeof(Icon), new PropertyMetadata(false));

    static Icon()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(Icon), new FrameworkPropertyMetadata(typeof(Icon)));
    }

    public Geometry? Geometry
    {
        get => (Geometry?)GetValue(GeometryProperty);
        set => SetValue(GeometryProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public bool IsFilled
    {
        get => (bool)GetValue(IsFilledProperty);
        set => SetValue(IsFilledProperty, value);
    }
}
