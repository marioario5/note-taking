using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NoteTaker.Core.Models;

namespace NoteTaker.App.Services;

public enum InsertPlacement
{
    Top,
    Middle,
    Bottom,
}

/// <summary>
/// Stamps generated content (graphs, code output) into the page background layer so it
/// exports, snapshots and indexes exactly like the rest of the page.
/// </summary>
public static class PageComposer
{
    public static ImageSource Compose(ImageSource? existing, ImageSource addition, InsertPlacement placement)
    {
        var target = PlacementRect(placement, addition);
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            if (existing is not null)
            {
                context.DrawImage(existing, new Rect(0, 0, PageGeometry.Width, PageGeometry.Height));
            }

            // No white backing plate. Graphs and Python output already rasterize their own
            // white card, so it was redundant for them — and on this app's dark paper it
            // painted a page-wide white slab behind anything pasted, which looked exactly
            // like the background had been wiped.
            context.DrawImage(addition, target);
        }

        var bitmap = new RenderTargetBitmap(
            (int)PageGeometry.Width,
            (int)PageGeometry.Height,
            96,
            96,
            PixelFormats.Pbgra32);

        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Where a newly added picture lands, in WORLD coordinates — the space the page canvas
    /// and the ink strokes live in.
    /// </summary>
    /// <remarks>
    /// <see cref="PlacementRect"/> is sheet-relative because <see cref="Compose"/> draws into
    /// a sheet-sized bitmap. Placed pictures are positioned on the world canvas instead,
    /// where the A4 sheet starts at (OriginX, OriginY) — about (99380, 99123). Handing the
    /// sheet-relative rectangle straight to the canvas put pasted images ~99,000 units left
    /// of the page: created, saved, and completely invisible.
    /// </remarks>
    public static Rect PlacementBounds(InsertPlacement placement, ImageSource image)
    {
        var sheet = PlacementRect(placement, image);
        return new Rect(
            PageGeometry.OriginX + sheet.X,
            PageGeometry.OriginY + sheet.Y,
            sheet.Width,
            sheet.Height);
    }

    /// <summary>
    /// Flattens the worksheet and the placed pictures into one bitmap. Used only by the
    /// paths that need a single image — export, and the chat capture. On screen the
    /// pictures stay separate elements so they remain movable.
    /// </summary>
    public static ImageSource? Flatten(ImageSource? worksheet, IReadOnlyList<PageImage> images)
    {
        if (worksheet is null && images.Count == 0)
        {
            return null;
        }

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            if (worksheet is not null)
            {
                context.DrawImage(worksheet, new Rect(0, 0, PageGeometry.Width, PageGeometry.Height));
            }

            // Placed pictures carry world coordinates; this bitmap is the sheet. Shift back
            // by the sheet's origin so a picture lands where it sits on the page rather than
            // ~99,000 units off the edge of the render.
            foreach (var image in images)
            {
                context.DrawImage(
                    Decode(image.Png),
                    new Rect(
                        image.X - PageGeometry.OriginX,
                        image.Y - PageGeometry.OriginY,
                        image.Width,
                        image.Height));
            }
        }

        var bitmap = new RenderTargetBitmap(
            (int)PageGeometry.Width,
            (int)PageGeometry.Height,
            96,
            96,
            PixelFormats.Pbgra32);

        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapImage Decode(byte[] png)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(png);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Draws one full-page layer over another, without rescaling either.</summary>
    public static ImageSource Overlay(ImageSource under, ImageSource over)
    {
        var full = new Rect(0, 0, PageGeometry.Width, PageGeometry.Height);
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            context.DrawImage(under, full);
            context.DrawImage(over, full);
        }

        var bitmap = new RenderTargetBitmap(
            (int)PageGeometry.Width,
            (int)PageGeometry.Height,
            96,
            96,
            PixelFormats.Pbgra32);

        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Encodes a layer for storage.</summary>
    public static byte[] ToPng(ImageSource image)
    {
        var bitmap = image as BitmapSource ?? Rasterize(image);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapSource Rasterize(ImageSource image)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(image, new Rect(0, 0, PageGeometry.Width, PageGeometry.Height));
        }

        var bitmap = new RenderTargetBitmap(
            (int)PageGeometry.Width,
            (int)PageGeometry.Height,
            96,
            96,
            PixelFormats.Pbgra32);

        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static Rect PlacementRect(InsertPlacement placement, ImageSource image)
    {
        const double margin = 90;
        var width = PageGeometry.Width - (margin * 2);
        var aspect = image.Height > 0 ? image.Height / image.Width : 0.6;
        var height = Math.Min(PageGeometry.Height / 2.4, width * aspect);

        var top = placement switch
        {
            InsertPlacement.Top => margin,
            InsertPlacement.Bottom => PageGeometry.Height - height - margin,
            _ => (PageGeometry.Height - height) / 2,
        };

        return new Rect(margin, top, width, height);
    }
}
