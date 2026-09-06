using NoteTaker.Core.Models;

namespace NoteTaker.Core.Tutor;

/// <summary>
/// When the hint ladder goes back to the bottom.
/// </summary>
/// <remarks>
/// The reveal is gated on a count of messages in the session, which stands in for how long the
/// student has been stuck. That proxy holds only while they are working ONE mistake. Left to run
/// it leaks: five messages into a page the reveal unlocks and stays unlocked, so a fresh
/// "is this right?" about a part they had only just started came back with the corrected
/// antiderivative and the final answer — the counter had passed the threshold on earlier,
/// unrelated work.
///
/// Two signals say the count no longer describes the work in front of them, and both are already
/// parsed out of the reply's verdict tag, so neither costs a token.
/// </remarks>
public static class HintLadder
{
    /// <summary>
    /// Whether the ladder should reset before the next turn.
    /// </summary>
    /// <param name="verdict">
    /// The tutor's judgement of the turn just finished, or null if it emitted no usable tag.
    /// </param>
    /// <param name="previousSkill">
    /// The skill the ladder was climbing, or empty if it has not started on one.
    /// </param>
    public static bool ShouldReset(SkillVerdict? verdict, string previousSkill)
    {
        // No tag this turn. Leave the count alone rather than guessing: resetting on silence
        // would hand a stuck student an endless supply of rung-one questions.
        if (verdict is null)
        {
            return false;
        }

        // That mistake is resolved, so the next one starts at rung one.
        if (verdict.Outcome == SkillOutcome.Right)
        {
            return true;
        }

        // A different kind of step entirely — the struggle behind the count was about something
        // else. The first judged skill of a session has nothing to differ from.
        return !string.IsNullOrEmpty(previousSkill)
            && !string.Equals(verdict.Skill, previousSkill, StringComparison.OrdinalIgnoreCase);
    }
}
