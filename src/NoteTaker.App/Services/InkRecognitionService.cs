using System.Numerics;
using System.Text;
using System.Windows.Ink;
using Windows.UI.Input.Inking;

namespace NoteTaker.App.Services;

/// <summary>
/// Converts WPF strokes into WinRT ink and runs the built-in Windows handwriting
/// recognizer. This only supplements search with whatever prose it can read; it is not
/// expected to transcribe equations, and failures are non-fatal.
/// </summary>
public static class InkRecognitionService
{
    public static bool IsAvailable
    {
        get
        {
            try
            {
                return new InkRecognizerContainer() is not null;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public static async Task<string> RecognizeAsync(StrokeCollection strokes)
    {
        if (strokes.Count == 0)
        {
            return string.Empty;
        }

        try
        {
            var builder = new InkStrokeBuilder();
            var container = new InkStrokeContainer();

            foreach (var stroke in strokes)
            {
                var points = new List<InkPoint>(stroke.StylusPoints.Count);
                foreach (var point in stroke.StylusPoints)
                {
                    points.Add(new InkPoint(
                        new Windows.Foundation.Point(point.X, point.Y),
                        Math.Clamp(point.PressureFactor, 0.05f, 1f)));
                }

                if (points.Count >= 2)
                {
                    container.AddStroke(builder.CreateStrokeFromInkPoints(points, Matrix3x2.Identity));
                }
            }

            var recognizer = new InkRecognizerContainer();
            var results = await recognizer.RecognizeAsync(container, InkRecognitionTarget.All);

            var builderText = new StringBuilder();
            foreach (var result in results)
            {
                var candidates = result.GetTextCandidates();
                if (candidates.Count > 0)
                {
                    builderText.Append(candidates[0]).Append(' ');
                }
            }

            return builderText.ToString().Trim();
        }
        catch (Exception)
        {
            // No recognition engine, or an unsupported stroke shape. Search still works
            // on page titles and visual similarity.
            return string.Empty;
        }
    }
}
