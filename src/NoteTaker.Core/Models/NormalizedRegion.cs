namespace NoteTaker.Core.Models;

/// <summary>
/// A rectangle expressed in 0..1 page coordinates. Storing regions normalized keeps
/// tutor highlights aligned to the ink when the page is zoomed or the window resized.
/// </summary>
public readonly record struct NormalizedRegion(double X, double Y, double Width, double Height)
{
    public static NormalizedRegion Clamped(double x, double y, double width, double height)
    {
        var left = Math.Clamp(x, 0d, 1d);
        var top = Math.Clamp(y, 0d, 1d);
        return new NormalizedRegion(
            left,
            top,
            Math.Clamp(width, 0d, 1d - left),
            Math.Clamp(height, 0d, 1d - top));
    }

    public bool IsEmpty => Width <= 0d || Height <= 0d;

    public NormalizedRegion Union(NormalizedRegion other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;

        var left = Math.Min(X, other.X);
        var top = Math.Min(Y, other.Y);
        var right = Math.Max(X + Width, other.X + other.Width);
        var bottom = Math.Max(Y + Height, other.Y + other.Height);
        return new NormalizedRegion(left, top, right - left, bottom - top);
    }

    /// <summary>Grows the region by <paramref name="padding"/> on all sides.</summary>
    /// <remarks>
    /// Deliberately does not clamp. These coordinates are fractions OF THE SHEET, and the
    /// canvas extends well past it — work done below or beside the sheet is legitimately at
    /// y &gt; 1. Clamping here dragged such regions back onto the sheet, putting the highlight
    /// somewhere the student never wrote.
    /// </remarks>
    public NormalizedRegion Inflate(double padding) =>
        new(X - padding, Y - padding, Width + (padding * 2), Height + (padding * 2));

    public bool Contains(double x, double y) =>
        x >= X && x <= X + Width && y >= Y && y <= Y + Height;

    public bool Intersects(NormalizedRegion other)
    {
        if (IsEmpty || other.IsEmpty)
        {
            return false;
        }

        return X < other.X + other.Width
            && X + Width > other.X
            && Y < other.Y + other.Height
            && Y + Height > other.Y;
    }

    /// <summary>Intersection-over-union; 0 when either region is empty or disjoint.</summary>
    public double IoU(NormalizedRegion other)
    {
        if (!Intersects(other))
        {
            return 0d;
        }

        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(X + Width, other.X + other.Width);
        var bottom = Math.Min(Y + Height, other.Y + other.Height);
        var intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        var union = (Width * Height) + (other.Width * other.Height) - intersection;
        return union <= 0 ? 0d : intersection / union;
    }

    /// <summary>
    /// Intersection over the smaller box. Catches nested / near-duplicate highlights
    /// where classic IoU stays low because one box is much larger.
    /// </summary>
    public double IoMin(NormalizedRegion other)
    {
        if (!Intersects(other))
        {
            return 0d;
        }

        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(X + Width, other.X + other.Width);
        var bottom = Math.Min(Y + Height, other.Y + other.Height);
        var intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        var smaller = Math.Min(Width * Height, other.Width * other.Height);
        return smaller <= 0 ? 0d : intersection / smaller;
    }
}
