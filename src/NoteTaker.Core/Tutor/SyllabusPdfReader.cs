using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace NoteTaker.Core.Tutor;

/// <summary>
/// Pulls the text out of a syllabus PDF so <see cref="SyllabusParser"/> can read the lessons.
/// </summary>
/// <remarks>
/// Locally, and for nothing. A syllabus is text in a container, and the alternative — rendering
/// each page and asking a vision model what it says — would spend real money re-reading
/// characters that are already sitting in the file. Setting up a course should not be the moment
/// the app first bills you.
///
/// Text extraction only. A scanned syllabus, photographed rather than typed, carries no text
/// layer and yields nothing here; that case wants OCR and is deliberately out of scope rather
/// than quietly turned into an API call.
/// </remarks>
public static partial class SyllabusPdfReader
{
    /// <summary>Reads every page's text, newline separated. Empty when there is no text layer.</summary>
    public static string ReadText(string path)
    {
        using var document = PdfDocument.Open(path);
        var lines = new List<string>();

        foreach (var page in document.GetPages())
        {
            // Word-level, not page.Text: PdfPig's raw page text runs the glyphs together in
            // content-stream order, which loses the line breaks the lesson numbers sit on.
            // Grouping words by their baseline rebuilds the lines as printed.
            foreach (var row in page.GetWords()
                         .GroupBy(w => Math.Round(w.BoundingBox.Bottom, 0))
                         .OrderByDescending(g => g.Key))
            {
                var text = string.Join(
                    ' ',
                    row.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text));

                if (!string.IsNullOrWhiteSpace(text))
                {
                    lines.Add(text.Trim());
                }
            }
        }

        return Typography().Replace(string.Join('\n', lines), "’");
    }

    /// <summary>A question mark standing where an apostrophe should be.</summary>
    /// <remarks>
    /// Subset fonts often ship the right single quote with no usable mapping back to Unicode, so
    /// a reader has nothing to return for it and substitutes a question mark: "Green's Theorem"
    /// arrives as "Green?s Theorem", and "Stokes' Theorem" as "Stokes? Theorem".
    ///
    /// Two shapes, both narrow. Between two letters a question mark is never real text. After an
    /// "s" and before a capitalised word it is a plural possessive — the one place a real
    /// question mark could sit instead is the end of a question, which a lesson title is not.
    /// </remarks>
    [GeneratedRegex(@"(?<=\p{L})\?(?=\p{L})|(?<=s)\?(?=\s\p{Lu})")]
    private static partial Regex Typography();
}
