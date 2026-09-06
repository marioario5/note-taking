using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NoteTaker.App.Services;

public static class ExportService
{
    public static void SavePng(string path, StrokeCollection strokes, ImageSource? background, double scale = 2.0)
    {
        var bitmap = PageRenderer.Render(strokes, background, scale);
        File.WriteAllBytes(path, PageRenderer.ToPng(bitmap));
    }

    /// <summary>
    /// Writes true vector output by exporting each stroke's outline geometry, so exported
    /// notes stay sharp at any zoom.
    /// </summary>
    public static void SaveSvg(string path, StrokeCollection strokes)
    {
        var svg = new StringBuilder();
        svg.AppendLine(
            $"""<svg xmlns="http://www.w3.org/2000/svg" width="{F(PageGeometry.Width)}" height="{F(PageGeometry.Height)}" viewBox="0 0 {F(PageGeometry.Width)} {F(PageGeometry.Height)}">""");
        svg.AppendLine("""<rect width="100%" height="100%" fill="#ffffff"/>""");
        // Strokes live in world space; shift the A4 sheet to the SVG origin.
        svg.AppendLine(
            $"""<g transform="translate({F(-PageGeometry.OriginX)} {F(-PageGeometry.OriginY)})">""");

        foreach (var stroke in strokes)
        {
            var data = ToPathData(stroke.GetGeometry());
            if (data.Length == 0)
            {
                continue;
            }

            var colour = stroke.DrawingAttributes.Color;
            var hex = $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";
            var opacity = colour.A / 255.0;

            svg.Append("""<path fill=""").Append('"').Append(hex).Append('"');
            if (opacity < 1)
            {
                svg.Append(" fill-opacity=\"").Append(F(opacity)).Append('"');
            }

            svg.Append(" d=\"").Append(data).AppendLine("\"/>");
        }

        svg.AppendLine("</g>");

        svg.AppendLine("</svg>");
        File.WriteAllText(path, svg.ToString(), Encoding.UTF8);
    }

    /// <summary>
    /// Writes a single-page PDF containing the flattened page. Kept deliberately small:
    /// one lossless Flate-compressed image, no external PDF dependency.
    /// </summary>
    public static void SavePdf(string path, StrokeCollection strokes, ImageSource? background, double scale = 2.0)
    {
        var bitmap = PageRenderer.Render(strokes, background, scale);
        var rgb = ToRgbBytes(bitmap, out var width, out var height);
        var compressed = Deflate(rgb);

        // A4 in PostScript points, matching the 150 DPI logical page.
        var pageWidth = PageGeometry.Width / 150.0 * 72.0;
        var pageHeight = PageGeometry.Height / 150.0 * 72.0;

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        var offsets = new List<long>();

        void WriteAscii(string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }

        void BeginObject(string body)
        {
            offsets.Add(stream.Position);
            WriteAscii(body);
        }

        WriteAscii("%PDF-1.4\n");

        BeginObject("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        BeginObject("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        BeginObject(
            $"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(pageWidth)} {F(pageHeight)}] " +
            "/Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");

        offsets.Add(stream.Position);
        WriteAscii(
            $"4 0 obj\n<< /Type /XObject /Subtype /Image /Width {width} /Height {height} " +
            $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {compressed.Length} >>\nstream\n");
        stream.Write(compressed, 0, compressed.Length);
        WriteAscii("\nendstream\nendobj\n");

        var content = $"q {F(pageWidth)} 0 0 {F(pageHeight)} 0 0 cm /Im0 Do Q\n";
        BeginObject($"5 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n");

        var xrefPosition = stream.Position;
        var xref = new StringBuilder();
        xref.Append("xref\n0 ").Append(offsets.Count + 1).Append('\n');
        xref.Append("0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            xref.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        xref.Append("trailer\n<< /Size ").Append(offsets.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xrefPosition).Append("\n%%EOF\n");

        WriteAscii(xref.ToString());
    }

    private static byte[] ToRgbBytes(BitmapSource bitmap, out int width, out int height)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Rgb24, null, 0);
        width = converted.PixelWidth;
        height = converted.PixelHeight;

        var stride = width * 3;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    private static string ToPathData(Geometry geometry)
    {
        var flattened = geometry.GetFlattenedPathGeometry(0.15, ToleranceType.Absolute);
        var builder = new StringBuilder();

        foreach (var figure in flattened.Figures)
        {
            builder.Append('M').Append(Point(figure.StartPoint));

            foreach (var segment in figure.Segments)
            {
                switch (segment)
                {
                    case PolyLineSegment poly:
                        foreach (var point in poly.Points)
                        {
                            builder.Append('L').Append(Point(point));
                        }

                        break;

                    case LineSegment line:
                        builder.Append('L').Append(Point(line.Point));
                        break;
                }
            }

            if (figure.IsClosed)
            {
                builder.Append('Z');
            }
        }

        return builder.ToString();
    }

    private static string Point(Point point) => $"{F(point.X)} {F(point.Y)}";

    private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
