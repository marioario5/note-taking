using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace NoteTaker.App.Controls;

/// <summary>
/// Sparingly scattered pinpricks of light for empty margins — ported from the Ellery design
/// system's <c>Starfield.jsx</c> so a given (seed, count) reproduces the exact same layout
/// here as in the mockup, rather than an arbitrary approximation. Decorative only; never
/// place behind reading text (mirrors the source component's own usage note).
/// </summary>
public sealed class StarfieldDecoration : Canvas
{
    public static readonly DependencyProperty CountProperty =
        DependencyProperty.Register(nameof(Count), typeof(int), typeof(StarfieldDecoration),
            new FrameworkPropertyMetadata(14, OnLayoutPropertyChanged));

    public static readonly DependencyProperty SeedProperty =
        DependencyProperty.Register(nameof(Seed), typeof(int), typeof(StarfieldDecoration),
            new FrameworkPropertyMetadata(7, OnLayoutPropertyChanged));

    /// <summary>"sparse" (0.55 resting opacity) or anything else (0.8) — matches the source's two-value enum.</summary>
    public static readonly DependencyProperty DensityProperty =
        DependencyProperty.Register(nameof(Density), typeof(string), typeof(StarfieldDecoration),
            new FrameworkPropertyMetadata("sparse", OnLayoutPropertyChanged));

    public StarfieldDecoration()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        SizeChanged += (_, _) => Rebuild();
        Loaded += (_, _) => Rebuild();
    }

    public int Count
    {
        get => (int)GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    public int Seed
    {
        get => (int)GetValue(SeedProperty);
        set => SetValue(SeedProperty, value);
    }

    public string Density
    {
        get => (string)GetValue(DensityProperty);
        set => SetValue(DensityProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((StarfieldDecoration)d).Rebuild();

    private void Rebuild()
    {
        Children.Clear();
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        // Same linear congruential generator as Starfield.jsx's rand(seed) — bit-for-bit
        // the same sequence, so placement is reproducible from (seed, count), not arbitrary.
        var state = (double)Seed;
        double Next()
        {
            state = ((state * 9301) + 49297) % 233280;
            return state / 233280;
        }

        var restingOpacity = string.Equals(Density, "sparse", StringComparison.OrdinalIgnoreCase) ? 0.55 : 0.8;
        // Same resources the rest of the theme uses (Accent = Star500, TextMuted = Peri300) —
        // reading the Color back off each brush keeps the glow tied to the token, not a
        // second hardcoded hex that could drift from it.
        var starBrush = (SolidColorBrush)FindResource("Accent");
        var periBrush = (SolidColorBrush)FindResource("TextMuted");

        for (var i = 0; i < Count; i++)
        {
            var top = Next() * 100;
            var left = Next() * 100;
            var size = 1.5 + (Next() * 3);
            var delaySeconds = Next() * 6;
            var durationSeconds = 3.5 + (Next() * 4);
            var isStar = i % 5 == 0;

            var dot = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = isStar ? starBrush : periBrush,
                Opacity = restingOpacity,
                Effect = new DropShadowEffect
                {
                    Color = isStar ? starBrush.Color : periBrush.Color,
                    BlurRadius = isStar ? 8 : 6,
                    ShadowDepth = 0,
                    Opacity = isStar ? 0.8 : 0.7,
                },
            };

            SetLeft(dot, ActualWidth * left / 100);
            SetTop(dot, ActualHeight * top / 100);
            Children.Add(dot);

            // ellery-twinkle: opacity 0.25 -> 1 -> 0.25, staggered per star. Before its own
            // delay elapses, the dot just shows its resting (density) opacity — matching how
            // a CSS animation's BeginTime/delay defers to the static value until it starts.
            var twinkle = new DoubleAnimationUsingKeyFrames
            {
                BeginTime = TimeSpan.FromSeconds(delaySeconds),
                Duration = TimeSpan.FromSeconds(durationSeconds),
                RepeatBehavior = RepeatBehavior.Forever,
            };
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            twinkle.KeyFrames.Add(new EasingDoubleKeyFrame(0.25, KeyTime.FromPercent(0), ease));
            twinkle.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(0.5), ease));
            twinkle.KeyFrames.Add(new EasingDoubleKeyFrame(0.25, KeyTime.FromPercent(1.0), ease));

            dot.BeginAnimation(OpacityProperty, twinkle);
        }
    }
}
