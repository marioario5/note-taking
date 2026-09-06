using System.Text.RegularExpressions;

namespace NoteTaker.App.Services;

/// <summary>
/// Decides which flagged finding a chat message is about: an explicit number in the text
/// ("error 2", "#3", "the third one"), "next" relative to whichever finding is currently
/// active, or — when neither applies — whatever's already active. Kept free of WPF and DB
/// types (findings are just an ordered list of stable ids) so the "no skips" guarantee below
/// can be exercised directly by the self-test gate rather than only by hand-testing the UI.
///
/// Everything is resolved against <paramref name="orderedIds"/> as passed in — the caller's
/// current badge numbering — never against a number or index remembered from an earlier
/// turn. That is what makes "next" safe when the count changes mid-conversation: fixing
/// error 1 mid-chat drops it from the list and renumbers everything after it down by one,
/// but "next" is resolved by looking up the *current* anchor's *current* position in the
/// *current* list every time, never by adding 1 to a cached number — so it cannot skip past
/// or double back onto a finding just because the numbers shifted underneath it.
/// </summary>
public static class ChatAnchorResolver
{
    private static readonly (string Word, int Number)[] OrdinalWords =
    [
        ("first", 1), ("second", 2), ("third", 3), ("fourth", 4), ("fifth", 5),
        ("sixth", 6), ("seventh", 7), ("eighth", 8), ("ninth", 9), ("tenth", 10),
    ];

    private static readonly Regex ExplicitNumberPattern = new(
        @"\b(?:error|number|finding|mistake|problem|#)\s*#?\s*(\d{1,2})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NextRequestPattern = new(
        @"\b(next|move on|moving on)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <param name="text">The student's typed message.</param>
    /// <param name="orderedIds">
    /// The currently flagged findings' stable ids, in the exact order their badges are
    /// numbered right now (index 0 is "error 1"). Empty when nothing is flagged.
    /// </param>
    /// <param name="currentAnchorId">Whichever finding the chat is currently anchored to, if any.</param>
    /// <returns>
    /// The id to anchor on, or null when <paramref name="orderedIds"/> is empty (nothing
    /// flagged — callers should fall back to a whole-page view rather than any one finding).
    /// </returns>
    public static long? Resolve(string text, IReadOnlyList<long> orderedIds, long? currentAnchorId)
    {
        if (orderedIds.Count == 0)
        {
            return null;
        }

        var explicitNumber = ParseExplicitFindingNumber(text);
        if (explicitNumber is { } number && number >= 1 && number <= orderedIds.Count)
        {
            return orderedIds[number - 1];
        }

        // An explicit number outside the current range ("error 9" with only 3 flagged)
        // falls through to the rules below rather than guessing which one they meant.
        if (NextRequestPattern.IsMatch(text))
        {
            var currentIndex = -1;
            if (currentAnchorId is { } anchorId)
            {
                for (var i = 0; i < orderedIds.Count; i++)
                {
                    if (orderedIds[i] == anchorId)
                    {
                        currentIndex = i;
                        break;
                    }
                }
            }

            // Anchor still on the list: step to whoever now sits right after it. Anchor
            // missing (it was just fixed and dropped off) or no anchor yet: land on the
            // first still-open finding — there is nothing valid left to step "next" from.
            return currentIndex >= 0 && currentIndex + 1 < orderedIds.Count
                ? orderedIds[currentIndex + 1]
                : orderedIds[0];
        }

        // Plain follow-up with no number or "next" — stay on whatever is already active.
        return currentAnchorId ?? orderedIds[0];
    }

    private static int? ParseExplicitFindingNumber(string text)
    {
        var match = ExplicitNumberPattern.Match(text);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var explicitNumber) && explicitNumber > 0)
        {
            return explicitNumber;
        }

        foreach (var (word, number) in OrdinalWords)
        {
            if (Regex.IsMatch(text, $@"\b{word}\b", RegexOptions.IgnoreCase))
            {
                return number;
            }
        }

        return null;
    }
}
