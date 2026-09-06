using System.Text.Json;
using NoteTaker.Core.Models;
using NoteTaker.Core.Tutor;

namespace NoteTaker.AI;

/// <summary>
/// Parses the region JSON from a vision reply. Models occasionally wrap output in prose
/// or code fences, so this recovers the JSON object rather than failing the whole check.
/// </summary>
public static class ScanResponseParser
{
    public static (IReadOnlyList<TutorRegionFinding> Findings, string Summary) Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return ([], string.Empty);
        }

        var json = ExtractJsonObject(content);
        if (json is null)
        {
            return ([], content.Trim());
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var findings = new List<TutorRegionFinding>();
            if (root.TryGetProperty("regions", out var regions) && regions.ValueKind == JsonValueKind.Array)
            {
                foreach (var region in regions.EnumerateArray())
                {
                    var finding = ReadFinding(region);
                    if (finding is not null)
                    {
                        findings.Add(finding);
                    }
                }
            }

            var summary = root.TryGetProperty("summary", out var summaryElement)
                ? summaryElement.GetString() ?? string.Empty
                : string.Empty;

            return (findings, summary);
        }
        catch (JsonException)
        {
            return ([], content.Trim());
        }
    }

    private static TutorRegionFinding? ReadFinding(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var x = ReadDouble(element, "x");
        var y = ReadDouble(element, "y");
        var w = ReadDouble(element, "w", "width");
        var h = ReadDouble(element, "h", "height");

        var label = ReadString(element, "label", "short_label") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        // Gemini's own detection convention is a 0..1000 box, not the 0..1 fraction this
        // prompt asks for. Flash-lite occasionally reverts to it: any coordinate past 1
        // means the whole box is on that scale, not a stray rounding error. Rescaling here
        // beats clamping into a dead zero-area box in the corner and losing the finding to
        // the ink hit-test downstream.
        if (x > 1d || y > 1d || w > 1d || h > 1d)
        {
            x /= 1000d;
            y /= 1000d;
            w /= 1000d;
            h /= 1000d;
        }

        var region = NormalizedRegion.Clamped(x, y, w, h);
        if (region.IsEmpty)
        {
            // A zero-area box cannot be drawn; give it a thin underline instead so the
            // student still sees where the model was pointing.
            region = NormalizedRegion.Clamped(x, y, Math.Max(w, 0.08), Math.Max(h, 0.02));
        }

        var topic = ReadString(element, "topic", "category");
        var reading = ReadString(element, "reading", "transcription");
        var where = ReadString(element, "where", "position");

        return new TutorRegionFinding(
            region,
            ReadSeverity(element),
            label.Trim(),
            string.IsNullOrWhiteSpace(topic) ? null : topic.Trim(),
            string.IsNullOrWhiteSpace(reading) ? null : reading.Trim(),
            string.IsNullOrWhiteSpace(where) ? null : where.Trim());
    }

    private static FeedbackSeverity ReadSeverity(JsonElement element)
    {
        var value = ReadString(element, "severity");
        return value?.Trim().ToLowerInvariant() switch
        {
            "major" or "high" or "critical" => FeedbackSeverity.Major,
            "info" or "note" or "low" => FeedbackSeverity.Info,
            _ => FeedbackSeverity.Minor,
        };
    }

    private static double ReadDouble(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number))
            {
                return number;
            }

            if (property.ValueKind == JsonValueKind.String &&
                double.TryParse(property.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return 0d;
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }
        }

        return null;
    }

    private static string? ExtractJsonObject(string content)
    {
        var text = content.Trim();

        var fence = text.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            var start = text.IndexOf('\n', fence);
            var end = text.IndexOf("```", fence + 3, StringComparison.Ordinal);
            if (start > 0 && end > start)
            {
                text = text[start..end].Trim();
            }
        }

        var open = text.IndexOf('{');
        var close = text.LastIndexOf('}');
        return open >= 0 && close > open ? text[open..(close + 1)] : null;
    }
}
