using NoteTaker.Core.Models;

namespace NoteTaker.Core.Tutor;

/// <summary>
/// Pulls the tutor's machine-readable verdict out of a reply, and returns the reply without it.
/// </summary>
/// <remarks>
/// Review needs a history of what the student is weak at, and the tutor already decides
/// correct-versus-wrong on every turn — that is rule one of its prompt. Rather than pay for a
/// second call to ask what it just concluded, it appends one tagged line and this removes it.
///
/// Pure and I/O-free so the tag format can be tested exhaustively without a model, in the same
/// spirit as <see cref="ChatHistoryWindow"/> and <see cref="InkLineSnapper"/>.
/// </remarks>
public static class SkillVerdictParser
{
    /// <summary>
    /// Delimiters chosen so they cannot collide with the reply itself: the tutor writes LaTeX,
    /// so anything built from $, \, {} or [] would eventually appear inside real mathematics.
    /// </summary>
    private const char Open = '⟦';

    private const char Close = '⟧';

    /// <summary>
    /// Folds spelling variants of one skill onto a single name.
    /// </summary>
    /// <remarks>
    /// A skill history only accumulates if the same skill yields the same string, and the model
    /// will not be consistent on its own: one live run returned "iterated integrals" and
    /// "iterated-integrals" for the same thing, which would have become two half-length
    /// histories and silently halved every count built on them. Normalising here rather than
    /// asking the prompt to be careful — the prompt already asks, and asking costs tokens on
    /// every turn to buy a guarantee this gives for free.
    /// </remarks>
    private static string NormaliseSkill(string raw)
    {
        var spaced = new string(raw.Select(c => c is '-' or '_' ? ' ' : c).ToArray());
        return string.Join(' ', spaced.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static (string Content, SkillVerdict? Verdict) Parse(string reply)
    {
        var open = reply.LastIndexOf(Open);
        if (open < 0)
        {
            return (reply, null);
        }

        // Everything from the opening bracket on is machine text, and it is stripped whether or
        // not it parses. A tag the model malformed still must not reach the student: losing the
        // verdict costs one data point, but rendering "⟦only-two|parts⟧" is a visible defect in
        // the one surface they actually read. Same for an unclosed bracket, which is what a
        // reply truncated by the output limit looks like.
        var visible = reply[..open].TrimEnd();

        var close = reply.IndexOf(Close, open);
        if (close < 0)
        {
            return (visible, null);
        }

        var parts = reply[(open + 1)..close].Split('|', 3);
        if (parts.Length < 3)
        {
            return (visible, null);
        }

        var outcome = parts[1].Trim().ToLowerInvariant() switch
        {
            "right" => SkillOutcome.Right,
            "wrong" => SkillOutcome.Wrong,
            _ => SkillOutcome.Unclear,
        };

        var verdict = new SkillVerdict(NormaliseSkill(parts[0]), outcome, parts[2].Trim());
        return (visible, verdict);
    }
}
