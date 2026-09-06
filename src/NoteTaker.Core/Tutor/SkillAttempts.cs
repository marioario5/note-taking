using NoteTaker.Core.Models;

namespace NoteTaker.Core.Tutor;

/// <summary>
/// Groups raw <see cref="SkillEvent"/> verdicts into attempts: one contiguous run of work on
/// one skill.
/// </summary>
/// <remarks>
/// This exists because a turn is not a data point. The tutor tags every reply, and a Socratic
/// dialogue circles the same skill for as long as it takes — so one problem produced nine rows
/// for one skill, and the student's first two problems looked like fifteen. Worse, the dialogue
/// only ends when the student is right, which means "wrong" is over-counted by construction and
/// every meter built on raw verdicts reads pessimistic however much data accumulates.
///
/// Collapsing a run into an attempt fixes both, and changes what is being measured to the thing
/// actually worth knowing: not whether the answer came out right — with a tutor it always does
/// eventually — but how much help it took to get there, and whether that is shrinking.
/// </remarks>
public static class SkillAttempts
{
    /// <summary>
    /// How long a skill can go untouched before returning to it counts as a fresh attempt.
    /// Long enough to survive a pause for thinking or a detour through a related skill; short
    /// enough that tomorrow's work on the same topic is not folded into today's.
    /// </summary>
    public static readonly TimeSpan AttemptGap = TimeSpan.FromMinutes(45);

    public static IReadOnlyList<SkillAttempt> Segment(IReadOnlyList<SkillEvent> events)
    {
        if (events.Count == 0)
        {
            return [];
        }

        var ordered = events
            .Where(e => !string.IsNullOrWhiteSpace(e.Skill))
            .OrderBy(e => e.CreatedAt)
            .ToList();

        var attempts = new List<SkillAttempt>();
        var run = new List<SkillEvent>();

        foreach (var item in ordered)
        {
            var continues = run.Count > 0
                && string.Equals(run[^1].Skill, item.Skill, StringComparison.OrdinalIgnoreCase)
                && item.CreatedAt - run[^1].CreatedAt <= AttemptGap;

            if (!continues && run.Count > 0)
            {
                Close(attempts, run);
                run.Clear();
            }

            run.Add(item);
        }

        Close(attempts, run);
        return attempts;
    }

    private static void Close(List<SkillAttempt> attempts, List<SkillEvent> run)
    {
        if (run.Count == 0)
        {
            return;
        }

        var corrections = run.Count(e => e.Outcome == SkillOutcome.Wrong);
        var judged = corrections + run.Count(e => e.Outcome == SkillOutcome.Right);

        // A run the tutor only ever answered "unclear" to is a conversation, not an attempt:
        // it asked a question and moved on without judging anything.
        if (judged == 0)
        {
            return;
        }

        attempts.Add(new SkillAttempt(
            run[0].Skill.Trim(),
            corrections,
            run.Count,
            run[0].CreatedAt,
            run[^1].CreatedAt));
    }
}
