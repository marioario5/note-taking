using System.Windows;
using NoteTaker.Core.Models;

namespace NoteTaker.App.Services;

/// <summary>
/// The writable world is a large ink surface. A smaller A4 "sheet" sits at its center for
/// PDF backgrounds, tutor regions, and exports — OneNote-style free drawing around it.
/// </summary>
public static class PageGeometry
{
    /// <summary>A4 sheet proportions at 150 DPI (PDF / tutor / export).</summary>
    public const double Width = 1240;

    public const double Height = 1754;

    /// <summary>Drawable world extent — matches the visible paper plane so pan ≠ draw lock.</summary>
    public const double WorldWidth = 200000;

    public const double WorldHeight = 200000;

    /// <summary>Top-left of the A4 sheet inside the world.</summary>
    public static double OriginX => (WorldWidth - Width) / 2;

    public static double OriginY => (WorldHeight - Height) / 2;

    public static Rect SheetBounds => new(OriginX, OriginY, Width, Height);

    public static Point SheetCenter => new(OriginX + (Width / 2), OriginY + (Height / 2));

    /// <summary>Normalized sheet region → rectangle in world coordinates.</summary>
    public static Rect ToPageRect(NormalizedRegion region) => new(
        OriginX + (region.X * Width),
        OriginY + (region.Y * Height),
        region.Width * Width,
        region.Height * Height);

    /// <summary>
    /// World rectangle → sheet-relative fractions, WITHOUT clamping to the sheet.
    /// </summary>
    /// <remarks>
    /// The clamping version is right for anything that must land on the page. It is wrong
    /// for describing where something actually is: the canvas extends far past the sheet, so
    /// work below it is genuinely at y &gt; 1, and clamping reports it as sitting on the
    /// bottom edge. Mapping a vision model's answers back onto the page needs the truth.
    /// </remarks>
    public static NormalizedRegion ToNormalizedUnclamped(Rect rect) => new(
        (rect.X - OriginX) / Width,
        (rect.Y - OriginY) / Height,
        rect.Width / Width,
        rect.Height / Height);

    /// <summary>World rectangle → normalized coords relative to the A4 sheet.</summary>
    public static NormalizedRegion ToNormalized(Rect rect) => NormalizedRegion.Clamped(
        (rect.X - OriginX) / Width,
        (rect.Y - OriginY) / Height,
        rect.Width / Width,
        rect.Height / Height);
}
