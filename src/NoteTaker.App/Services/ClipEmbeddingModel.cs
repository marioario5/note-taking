using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NoteTaker.Core.Abstractions;
using NoteTaker.Core.Search;

namespace NoteTaker.App.Services;

/// <summary>
/// Runs a CLIP image encoder through ONNX Runtime for stronger page similarity than the
/// thumbnail model. The model file is not shipped; point Settings at a downloaded
/// image-encoder ONNX and this takes over automatically.
/// </summary>
public sealed class ClipEmbeddingModel : IEmbeddingModel, IDisposable
{
    private const int InputSize = 224;

    // Standard CLIP normalization constants.
    private static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] StdDev = [0.26862954f, 0.26130258f, 0.27577711f];

    private readonly InferenceSession? _session;
    private readonly string? _inputName;

    public ClipEmbeddingModel(string modelPath)
    {
        try
        {
            if (File.Exists(modelPath))
            {
                _session = new InferenceSession(modelPath);
                _inputName = _session.InputMetadata.Keys.FirstOrDefault();
            }
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or IOException or BadImageFormatException)
        {
            _session = null;
        }
    }

    public int Dimensions { get; private set; } = 512;

    public bool IsAvailable => _session is not null && _inputName is not null;

    public Task<float[]> EmbedImageAsync(byte[] png, CancellationToken ct = default)
    {
        if (_session is null || _inputName is null || png.Length == 0)
        {
            return Task.FromResult(Array.Empty<float>());
        }

        try
        {
            var tensor = BuildTensor(png);
            using var results = _session.Run(
                [NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);

            var output = results.FirstOrDefault()?.AsEnumerable<float>().ToArray();
            if (output is null || output.Length == 0)
            {
                return Task.FromResult(Array.Empty<float>());
            }

            Dimensions = output.Length;
            return Task.FromResult(VectorMath.Normalize(output));
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or ArgumentException or NotSupportedException)
        {
            return Task.FromResult(Array.Empty<float>());
        }
    }

    private static DenseTensor<float> BuildTensor(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        var rgb = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);
        var scaled = new TransformedBitmap(
            rgb,
            new ScaleTransform(
                InputSize / (double)rgb.PixelWidth,
                InputSize / (double)rgb.PixelHeight));

        var stride = InputSize * 3;
        var pixels = new byte[stride * InputSize];
        scaled.CopyPixels(pixels, stride, 0);

        var tensor = new DenseTensor<float>([1, 3, InputSize, InputSize]);
        for (var y = 0; y < InputSize; y++)
        {
            for (var x = 0; x < InputSize; x++)
            {
                var offset = (y * stride) + (x * 3);
                // Source is BGR; CLIP expects channel-first RGB.
                for (var channel = 0; channel < 3; channel++)
                {
                    var value = pixels[offset + (2 - channel)] / 255f;
                    tensor[0, channel, y, x] = (value - Mean[channel]) / StdDev[channel];
                }
            }
        }

        return tensor;
    }

    public void Dispose() => _session?.Dispose();
}
