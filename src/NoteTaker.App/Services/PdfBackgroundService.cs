using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NoteTaker.App.Services;

/// <summary>
/// Renders a PDF page to a bitmap that sits behind the ink layer. The PDF is never
/// modified; annotations live in our own ink store and are composited on export.
/// </summary>
public static class PdfBackgroundService
{
    public static int GetPageCount(string pdfPath)
    {
        try
        {
            using var stream = File.OpenRead(pdfPath);
            return PDFtoImage.Conversion.GetPageCount(stream);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return 0;
        }
    }

    public static ImageSource? Render(string pdfPath, int pageIndex)
    {
        try
        {
            using var stream = File.OpenRead(pdfPath);
            using var bitmap = PDFtoImage.Conversion.ToImage(
                stream,
                page: pageIndex,
                options: new PDFtoImage.RenderOptions(
                    Width: (int)PageGeometry.Width,
                    Height: (int)PageGeometry.Height,
                    WithAspectRatio: false));

            using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
            using var memory = new MemoryStream(data.ToArray());

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = memory;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Embedded text layer, used to make imported problem sets searchable.</summary>
    public static string ExtractText(string pdfPath, int pageIndex)
    {
        // PDFium exposes rendering here but not text extraction, so PDF text search is
        // limited to what the importer records. Ink recognition covers the rest.
        _ = pdfPath;
        _ = pageIndex;
        return string.Empty;
    }
}
