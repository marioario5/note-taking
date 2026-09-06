using NoteTaker.Core.Models;

namespace NoteTaker.Core.Tutor;

/// <summary>
/// Chooses how much of a chat thread to resend on the next turn.
/// </summary>
/// <remarks>
/// Every turn used to resend the entire thread — <c>GetMessagesAsync</c> has no LIMIT — so a
/// long conversation grew linearly in both cost and time-to-first-token. Measured on a real
/// day's usage: chat was 82% of all spend, at 3,600–6,300 input tokens per turn, and 97% of
/// every token the app spent was input rather than output. Trimming this is worth more than
/// any model change.
///
/// Dropping the single oldest message every turn would be the obvious approach, and it was
/// avoided here on the grounds that prompt caching keys on the exact bytes of the prefix, so a
/// cut point moving by one every turn would invalidate the cache on every call. Blocks keep
/// the cut point still for <see cref="TrimBlock"/> turns at a stretch.
///
/// That reasoning is sound but the cache it protects turned out not to exist on this path:
/// Gemini's OpenAI-compatible endpoint 400s on the explicit cache fields, and two identical
/// 3,646-token requests measured against it came back with no cached-token field at all. So
/// block trimming is kept for its own sake — a stable cut point is easier to reason about, and
/// it costs nothing — but it is no longer a reason to hold a large window.
///
/// Dropping old turns is safe here because each turn re-anchors itself: the Socratic prompt
/// has every message start with a bracketed tag naming what the attached image currently
/// shows, and a fresh crop rides along with it. The model is told to trust that tag over
/// anything it inferred earlier, so the oldest exchanges are not load-bearing.
/// </remarks>
public static class ChatHistoryWindow
{
    /// <summary>Resend everything up to this many messages; only trim past it.</summary>
    /// <remarks>
    /// Halved from 16 once the cache justifying the larger window was measured and found
    /// absent. Eight messages is four full exchanges — enough for the thread to make sense,
    /// and every message past it was being paid for at full price on every single turn.
    /// </remarks>
    public const int MaxMessages = 8;

    /// <summary>Granularity of trimming; the cut point only advances in multiples of this.</summary>
    public const int TrimBlock = 4;

    /// <summary>
    /// The tail of <paramref name="history"/> worth resending, oldest first.
    /// </summary>
    /// <remarks>
    /// The cut point is computed as a function of <c>history.Count</c> alone, rounded down to
    /// a block boundary — not incremented per call — so two threads at the same length always
    /// agree on where the prefix starts, and one thread's own cut point does not creep forward
    /// one message at a time as it grows.
    /// </remarks>
    public static IReadOnlyList<TutorMessage> Select(IReadOnlyList<TutorMessage> history)
    {
        if (history.Count <= MaxMessages)
        {
            return history;
        }

        // How far past the cap we are, rounded UP to the next block — so the very first turn
        // that exceeds the cap already drops a full block rather than trickling out one
        // message at a time until the next boundary.
        var over = history.Count - MaxMessages;
        var drop = ((over + TrimBlock - 1) / TrimBlock) * TrimBlock;

        return drop >= history.Count
            ? []
            : history.Skip(drop).ToList();
    }
}
