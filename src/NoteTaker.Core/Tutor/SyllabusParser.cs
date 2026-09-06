using System.Text.RegularExpressions;

namespace NoteTaker.Core.Tutor;

/// <summary>One lesson read out of a pasted syllabus or table of contents.</summary>
/// <param name="Number">The textbook's own numbering, "5.6", or empty when unnumbered.</param>
/// <param name="Unit">The chapter the number implies, or null when there is no numbering.</param>
public sealed record SyllabusEntry(string Number, string Title, int? Unit)
{
    /// <summary>What the lesson is called in the app: the textbook's name for it.</summary>
    public string DisplayName => Number.Length == 0 ? Title : $"{Number} {Title}";
}

/// <summary>
/// Reads a pasted table of contents into lessons, locally and for free.
/// </summary>
/// <remarks>
/// Free is the point. Setting up a course is the one chore standing between the student and a
/// working Review, and a table of contents is already structured text — "5.6 Center of Mass"
/// parses with a regex. Sending it to a model to be read would spend tokens on a lookup the
/// characters already answer. A photographed syllabus is the only case that genuinely needs
/// vision, and it stays a separate, opt-in path.
/// </remarks>
public static partial class SyllabusParser
{
    [GeneratedRegex(@"^\s*(\d{1,2})\.(\d{1,2})\s+(.*?)\s*$")]
    private static partial Regex NumberedLesson();

    /// <summary>Dot leaders and the page number they run to: "…… 312".</summary>
    [GeneratedRegex(@"(?:\s*\.{2,}\s*|\s+)\d{1,4}$")]
    private static partial Regex TrailingPageNumber();

    /// <summary>
    /// Copies of the lesson number trailing its own title: "5.1 Double Integrals 5.1 5.1".
    /// </summary>
    /// <remarks>
    /// A content menu is usually a table — the lesson in one column, a progress or checkbox
    /// column beside it repeating the number. Those cells share a baseline with the title, so
    /// any reader that rebuilds lines from positions hands them over as one line, and the
    /// number lands in the middle of every lesson name.
    /// </remarks>
    [GeneratedRegex(@"(?:\s+\d{1,2}\.\d{1,2})+$")]
    private static partial Regex TrailingNumberEchoes();

    public static IReadOnlyList<SyllabusEntry> Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var lines = text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        var numbered = new List<SyllabusEntry>();
        var bare = new List<SyllabusEntry>();

        foreach (var line in lines)
        {
            var match = NumberedLesson().Match(line);
            if (match.Success)
            {
                var title = Clean(match.Groups[3].Value);

                // A lesson's title has words in it. Requiring a letter is what separates
                // "5.1 Double Integrals over Rectangular Regions" from the week-by-week
                // schedule further down the same syllabus, whose rows read "5.1 - 5.2" and
                // would otherwise import as a lesson called "- 5.2".
                if (title.Any(char.IsLetter))
                {
                    numbered.Add(new SyllabusEntry(
                        $"{match.Groups[1].Value}.{match.Groups[2].Value}",
                        title,
                        int.Parse(match.Groups[1].Value)));
                }

                continue;
            }

            var plain = Clean(line);
            if (plain.Length > 0)
            {
                bare.Add(new SyllabusEntry(string.Empty, plain, null));
            }
        }

        // Numbering, once present, is what separates a lesson from the prose around it: chapter
        // headings, a grading policy, a stray sentence of preamble. Only when a syllabus carries
        // no numbering anywhere — an SAT course listing Reading, Writing, Math — is every line
        // taken at face value.
        var chosen = numbered.Count > 0 ? numbered : bare;

        return chosen
            .DistinctBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The course's own name, when the syllabus states one — "Calculus lll".
    /// </summary>
    /// <remarks>
    /// A syllabus says what course it is for on its first page, so importing one should not also
    /// require typing the subject's name. The label and its value are usually on separate lines,
    /// because the page lays them out as a two-column table.
    /// </remarks>
    public static string? CourseTitle(string text)
    {
        var lines = text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < lines.Length; i++)
        {
            var match = CourseTitleLabel().Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            var sameLine = match.Groups[1].Value.Trim();
            if (sameLine.Length > 0)
            {
                return sameLine;
            }

            for (var next = i + 1; next < lines.Length && next <= i + 2; next++)
            {
                var value = lines[next].Trim();

                // "Course Title:" followed by "Course Code:" means the value cell was empty.
                if (value.Length > 0 && !CourseTitleLabel().IsMatch(value) && !value.EndsWith(':'))
                {
                    return value;
                }
            }

            return null;
        }

        return null;
    }

    [GeneratedRegex(@"^\s*Course\s*(?:Title|Name)\s*:?\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex CourseTitleLabel();

    /// <summary>
    /// A lesson's number as something sortable: 5.6 before 5.7, and 5.10 after 5.9.
    /// </summary>
    /// <remarks>
    /// Lessons have to fall into textbook order on their own. Sorting by the order they were
    /// created puts a lesson added later at the end of its unit — add 5.6 after importing and it
    /// lands below 5.7 — and sorting by name is worse still, because "5.10" sorts above "5.9"
    /// when the comparison is textual. The number in the name is the real order; this reads it.
    /// </remarks>
    public static (int Unit, int Lesson)? NumberOf(string lessonName)
    {
        var match = NumberedLesson().Match(lessonName);
        return match.Success
            ? (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value))
            : null;
    }

    /// <summary>The unit a lesson name belongs to, read from its leading number.</summary>
    public static int? UnitOf(string lessonName)
    {
        var match = NumberedLesson().Match(lessonName);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    private static string Clean(string value)
    {
        var text = TrailingNumberEchoes().Replace(value.Trim(), string.Empty);
        return TrailingPageNumber().Replace(text.Trim(), string.Empty).Trim();
    }
}
