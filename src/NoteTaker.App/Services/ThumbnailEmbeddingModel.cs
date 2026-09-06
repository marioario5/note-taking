using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NoteTaker.Core.Abstractions;
using NoteTaker.Core.Search;

namespace NoteTaker.App.Services;

/// <summary>
/// Default page-similarity model with no external dependencies: it reduces the page to a
/// coarse ink-density grid. That captures layout and structure well enough to surface
/// "pages like this one", and it works offline on day one. A CLIP model can be pointed at
/// in Settings for stronger semantic similarity.
/// </summary>
public sealed class ThumbnailEmbeddingModel : IEmbeddingModel
{
    private const int Grid = 16;

    public int Dimensions => Grid * Grid;

    public bool IsAvailable => true;

    public Task<float[]> EmbedImageAsync(byte[] png, CancellationToken ct = default)
    {
        if (png.Length == 0)
        {
            return Task.FromResult(Array.Empty<float>());
        }

        try
        {
            using var stream = new MemoryStream(png);
            var frame = BitmapFrame.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            var gray = new FormatConvertedBitmap(frame, PixelFormats.Gray8, null, 0);
            var scaled = new TransformedBitmap(
                gray,
                new ScaleTransform(
                    Grid / (double)gray.PixelWidth,
                    Grid / (double)gray.PixelHeight));

            var stride = Grid;
            var pixels = new byte[Grid * Grid];
            scaled.CopyPixels(pixels, stride, 0);

            var vector = new float[Dimensions];
            for (var i = 0; i < pixels.Length; i++)
            {
                // Invert so ink counts as signal and blank paper as zero.
                vector[i] = (255 - pixels[i]) / 255f;
            }

            return Task.FromResult(VectorMath.Normalize(vector));
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
        {
            return Task.FromResult(Array.Empty<float>());
        }
    }
}
