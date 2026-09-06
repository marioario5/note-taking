using System.IO;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NoteTaker.Core.Models;

namespace NoteTaker.App.Services;

/// <summary>
/// Rasterizes a page from its source data rather than from the on-screen visual, so the
/// result is identical regardless of zoom level and never includes tutor highlights.
/// </summary>
public static class PageRenderer
{
    public static BitmapSource Render(
        StrokeCollection strokes,
        ImageSource? background,
        double scale = 1.0,
        Rect? clip = null,
        bool ocrContrast = false,
        bool? drawBackground = null)
    {
        // Whether the background layer is painted used to be decided implicitly by
        // ocrContrast, which coupled two unrelated ideas: "use OCR colours" and "hide the
        // background". Chat now needs OCR colours WITH the background (a pasted picture is
        // context the student is asking about), while the flagging scan needs neither.
        var paintBackground = drawBackground ?? !ocrContrast;
        var area = clip ?? PageGeometry.SheetBounds;
        var visual = new DrawingVisual();
        var drawStrokes = ocrContrast ? ForOcr(strokes) : strokes;

        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new TranslateTransform(-area.X, -area.Y));

            // Chat/vision: pure white + black ink. Cream-on-dark (or cream-on-white) made
            // models invent digits and complain the image was "faint".
            //
            // Fills the area being rendered, not the A4 sheet. When the capture follows the
            // student's work out onto open canvas, a sheet-sized card leaves everything
            // beyond it transparent — the ink would sit on nothing.
            context.DrawRectangle(Brushes.White, null, area);

            if (background is not null && paintBackground)
            {
                context.DrawImage(background, PageGeometry.SheetBounds);
            }

            drawStrokes.Draw(context);
            context.Pop();
        }

        // Last line of defence: no caller can allocate an unbounded surface, whatever clip
        // and scale it passes. Silently producing a smaller image beats taking the app down.
        var pixelWidth = Math.Clamp((int)Math.Round(area.Width * scale), 1, 4096);
        var pixelHeight = Math.Clamp((int)Math.Round(area.Height * scale), 1, 4096);

        var bitmap = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            96 * scale,
            96 * scale,
            PixelFormats.Pbgra32);

        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public static byte[] ToPng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// The rectangle that actually holds something — ink and placed pictures — padded a
    /// little, falling back to the A4 sheet when the page is empty.
    /// </summary>
    /// <remarks>
    /// The canvas is effectively infinite and the sheet is one fixed rectangle inside it, so
    /// "the page" and "where the student is working" are frequently different places. Every
    /// capture used to clip to the sheet, which meant panning somewhere with room and doing
    /// a page of working produced a picture of the empty sheet — the tutor would describe
    /// whatever happened to be sitting at the origin and report seeing no handwriting.
    /// </remarks>
    public static Rect ContentBounds(
        StrokeCollection strokes,
        IReadOnlyList<PageImage>? images = null,
        Rect? within = null)
    {
        var bounds = Rect.Empty;

        // Only what is inside the window of interest, when one is given. Unioning everything
        // on the page lets a single stray mark in a far corner of a 200,000-unit world
        // stretch the rectangle until the actual work renders as hairlines.
        var limit = within ?? Rect.Empty;

        foreach (Stroke stroke in strokes)
        {
            var strokeBounds = stroke.GetBounds();
            if (within is null || strokeBounds.IntersectsWith(limit))
            {
                bounds.Union(strokeBounds);
            }
        }

        if (images is not null)
        {
            foreach (var image in images)
            {
                var imageBounds = new Rect(image.X, image.Y, image.Width, image.Height);
                if (within is null || imageBounds.IntersectsWith(limit))
                {
                    bounds.Union(imageBounds);
                }
            }
        }

        if (bounds.IsEmpty)
        {
            return within ?? PageGeometry.SheetBounds;
        }

        // Deliberately NOT clipped back to the window. The window decides WHICH marks count,
        // not how much of them is shown — clipping cut off the bottom of a page of working
        // that continued just past the visible edge. Anything far away was already excluded
        // by the intersection test above, so the result stays bounded.

        bounds.Inflate(64, 64);

        // Never smaller than a comfortable reading window, or one short line becomes a hugely
        // magnified crop with no surrounding context.
        if (bounds.Width < 600)
        {
            bounds.Inflate((600 - bounds.Width) / 2, 0);
        }

        if (bounds.Height < 400)
        {
            bounds.Inflate(0, (400 - bounds.Height) / 2);
        }

        return bounds;
    }

    /// <summary>No render may exceed this on either side, whatever it was asked for.</summary>
    private const double MaxRenderPixels = 2400;

    /// <summary>Renders an explicit world rectangle — used to follow the work off-sheet.</summary>
    /// <remarks>
    /// The scale is bounded by BOTH dimensions, and then by an absolute pixel ceiling. A
    /// stray picture left at a bad coordinate stretched the content rectangle across ~100,000
    /// world units; with only a lower bound on scale that asked for a bitmap tens of
    /// thousands of pixels wide and took the whole app down with an out-of-memory kill. A
    /// capture being too small to read is a bad picture; a capture that cannot be allocated
    /// is a crash, so the ceiling wins.
    /// </remarks>
    public static byte[] RenderAreaPng(
        StrokeCollection strokes,
        ImageSource? background,
        Rect area,
        double targetWidth,
        bool ocrContrast = true,
        bool? drawBackground = null,
        double minScale = 0)
    {
        var width = Math.Max(area.Width, 1);
        var height = Math.Max(area.Height, 1);

        var scale = targetWidth / width;

        // targetWidth is a token budget, not a judgement about legibility, and on a wide area
        // it silently becomes one: dividing a fixed width by a growing area shrinks the ink.
        // A capture measured at 1000x440 over a ~2270-unit area put the ink at 44% of the size
        // it was written, and the tutor read a handwritten "x/2" as "1/2" and spent a turn
        // asking the student to add an x that was already there. RenderRegionPng has always
        // floored its scale at 1.0 for this reason; this path did not, so which of the two a
        // turn happened to take decided whether the ink survived. MaxRenderPixels still wins
        // below — the floor may cost tokens, never an oversized render.
        scale = Math.Max(scale, minScale);

        scale = Math.Min(scale, MaxRenderPixels / height);
        scale = Math.Min(scale, MaxRenderPixels / width);
        scale = Math.Clamp(scale, 0.01, 5.0);

        return ToPng(Render(strokes, background, scale, area, ocrContrast, drawBackground));
    }

    public static byte[] RenderPagePng(
        StrokeCollection strokes,
        ImageSource? background,
        double targetWidth = 1000,
        bool ocrContrast = false,
        bool? drawBackground = null)
    {
        var scale = targetWidth / PageGeometry.Width;
        return ToPng(Render(strokes, background, scale, ocrContrast: ocrContrast, drawBackground: drawBackground));
    }

    public static byte[] RenderRegionPng(
        StrokeCollection strokes,
        ImageSource? background,
        NormalizedRegion region,
        double targetWidth = 1400,
        bool ocrContrast = true,
        bool? drawBackground = null)
    {
        var rect = PageGeometry.ToPageRect(region);
        if (rect.Width < 1 || rect.Height < 1)
        {
            return RenderPagePng(strokes, background, targetWidth, ocrContrast, drawBackground);
        }

        // Same reasoning as InkContentRegion: no sheet intersection. A crop of a finding that
        // sits off the A4 rectangle is still the thing being discussed.
        rect.Inflate(36, 36);
        if (rect.IsEmpty)
        {
            return RenderPagePng(strokes, background, targetWidth, ocrContrast, drawBackground);
        }

        // Oversample a tight crop so a small region stays readable, but never enlarge past
        // the caller's target — the floor used to be 2.0, which quietly turned targetWidth
        // from a ceiling into a suggestion. A whole-page crop (rect ~1240 units) asked for
        // 1300px and got 1240 x 2.0 = 2480px: nearly four times the tiles a vision model
        // bills for, on the single most frequent call in the app. A saved copy of exactly
        // what the model receives is what surfaced it.
        var scale = Math.Clamp(targetWidth / Math.Max(rect.Width, 1), 1.0, 5.0);
        return ToPng(Render(strokes, background, scale, rect, ocrContrast, drawBackground));
    }

    /// <param name="images">
    /// Pasted figures and generated stamps. Required for the same reason the strokes are: a
    /// crop built from ink alone slices whatever picture the student is working from, and the
    /// tutor is then asked about a diagram it can only half see.
    /// </param>
    public static NormalizedRegion? InkContentRegion(
        StrokeCollection strokes,
        NormalizedRegion? focus = null,
        IReadOnlyList<PageImage>? images = null)
    {
        if (strokes.Count == 0 && (images is null || images.Count == 0))
        {
            return focus is { IsEmpty: false } ? focus : null;
        }

        var focusRect = focus is { IsEmpty: false } focusValue
            ? PageGeometry.ToPageRect(focusValue.Inflate(0.08))
            : Rect.Empty;

        // Finding chats: stay tight on the flagged region so digits stay readable.
        if (focus is { IsEmpty: false } focusOnly)
        {
            var local = Rect.Empty;
            foreach (Stroke stroke in strokes)
            {
                var strokeBounds = stroke.GetBounds();
                if (!strokeBounds.IntersectsWith(focusRect))
                {
                    continue;
                }

                local.Union(strokeBounds);
            }

            foreach (var imageBounds in ImageBounds(images))
            {
                if (imageBounds.IntersectsWith(focusRect))
                {
                    local.Union(imageBounds);
                }
            }

            if (!local.IsEmpty)
            {
                local = GrowToWholeMarks(local, strokes, images);
                local.Inflate(40, 32);
                // Don't intersect with SheetBounds: the student's work lives off-sheet, and
                // clipping to the A4 boundary drops everything they actually wrote, leaving
                // only the (empty) intersection. This cascades to the fallback, which sends
                // a picture of the vision model's OWN guess box instead of their expression.
                //
                // Unclamped for the same reason, and it is the easier half to miss: the
                // explicit Rect.Intersect was removed here but ToNormalized still ran the
                // result through NormalizedRegion.Clamped, which caps width at 1 - left. The
                // clipping simply moved from a visible call into the coordinate conversion,
                // and a saved crop showed the cost — a set-builder line running off the
                // sheet's right edge arrived with its upper bound sliced off, so the tutor
                // was asked to judge a definition it could only see half of.
                return PageGeometry.ToNormalizedUnclamped(local);
            }

            return focusOnly.Inflate(0.1);
        }

        var bounds = Rect.Empty;
        foreach (Stroke stroke in strokes)
        {
            bounds.Union(stroke.GetBounds());
        }

        foreach (var imageBounds in ImageBounds(images))
        {
            bounds.Union(imageBounds);
        }

        if (bounds.IsEmpty)
        {
            return null;
        }

        bounds.Inflate(56, 48);

        // Deliberately NOT intersected with the sheet. Work done on open canvas below or
        // beside the A4 rectangle is still the student's work; clipping to the sheet
        // returned either nothing or just the fragment that happened to overlap, which is
        // how the tutor ended up describing a checkpoint image at the origin while insisting
        // it could not see any handwriting.
        return bounds.IsEmpty ? null : PageGeometry.ToNormalizedUnclamped(bounds);
    }

    private static IEnumerable<Rect> ImageBounds(IReadOnlyList<PageImage>? images)
    {
        if (images is null)
        {
            yield break;
        }

        foreach (var image in images)
        {
            yield return new Rect(image.X, image.Y, image.Width, image.Height);
        }
    }

    /// <summary>
    /// Grows a crop until it holds every mark it touches whole, rather than in part.
    /// </summary>
    /// <remarks>
    /// Selecting only the marks that meet the focus box cuts straight through anything
    /// straddling its edge. A saved crop showed the cost: a set-builder line qualified on its
    /// left half and arrived with the tail sliced off, so where the student had written
    /// "x/2 &lt;= y &lt;= 1" the tutor received "x/2 &lt;=" and nothing more. It then spent a
    /// whole exchange asking them to re-read the upper bound on y — from a line whose upper
    /// bound had been cropped away — and insisted their correct answer was wrong. From the
    /// model's side that is not a mistake; it is the only conclusion available from the
    /// picture it was handed.
    ///
    /// Growth converges on its own: it stops as soon as the next mark is further than
    /// <see cref="SameLineReachX"/> away, which is what the end of a line looks like. The
    /// pass limit is only a runaway guard, not a budget — capping it low just moves the
    /// truncation from the focus box to the cap, which is the same bug wearing a hat.
    /// </remarks>
    /// <summary>How far sideways a mark can sit and still count as part of the same line.</summary>
    /// <remarks>
    /// Testing bare overlap does not work: the glyphs in "x/2 &lt;= y &lt;= 1" are separate
    /// strokes with clear air between them, so a rect holding the first character touches
    /// nothing and stops there. The reach has to span an inter-character gap. Vertically it
    /// stays tight, so reaching along a line never climbs onto the line above or below.
    /// </remarks>
    private const double SameLineReachX = 60;

    private const double SameLineReachY = 10;

    private static Rect GrowToWholeMarks(
        Rect rect,
        StrokeCollection strokes,
        IReadOnlyList<PageImage>? images)
    {
        for (var pass = 0; pass < 64; pass++)
        {
            var probe = rect;
            probe.Inflate(SameLineReachX, SameLineReachY);

            var grown = rect;

            foreach (Stroke stroke in strokes)
            {
                var bounds = stroke.GetBounds();
                if (bounds.IntersectsWith(probe))
                {
                    grown.Union(bounds);
                }
            }

            foreach (var bounds in ImageBounds(images))
            {
                if (bounds.IntersectsWith(probe))
                {
                    grown.Union(bounds);
                }
            }

            if (grown == rect)
            {
                break;
            }

            rect = grown;
        }

        return rect;
    }

    /// <summary>Black, slightly thicker strokes on white — maximum OCR contrast for chat models.</summary>
    private static StrokeCollection ForOcr(StrokeCollection source)
    {
        var copy = new StrokeCollection();
        foreach (Stroke stroke in source)
        {
            var clone = stroke.Clone();
            clone.DrawingAttributes.Color = Colors.Black;
            clone.DrawingAttributes.IsHighlighter = false;
            // Fatten just enough to survive the downscale to ~1000px, and no further. The
            // old 1.35x with a 2.8 floor was self-defeating on exactly the work this app
            // exists to read: a stroke is drawn as its tip stamped along the path, so a
            // 2.8-unit nib closes any counter narrower than that — and a superscript digit
            // is only ~12-20 units wide. The vision model was being handed small digits with
            // their bowls filled in solid, then blamed for misreading them.
            clone.DrawingAttributes.Width = Math.Max(clone.DrawingAttributes.Width * 1.15, 1.8);
            clone.DrawingAttributes.Height = Math.Max(clone.DrawingAttributes.Height * 1.15, 1.8);
            copy.Add(clone);
        }

        return copy;
    }
}
