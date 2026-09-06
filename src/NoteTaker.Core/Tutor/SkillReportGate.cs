using NoteTaker.Core.Models;

namespace NoteTaker.Core.Tutor;

/// <summary>Whether a topic has earned a freshly written report, and why not when it has not.</summary>
public readonly record struct ReportDecision(bool Generate, string Reason)
{
    public static ReportDecision Yes => new(true, string.Empty);

    public static ReportDecision No(string reason) => new(false, reason);
}

/// <summary>
/// The guard in front of the only part of Review that costs money.
/// </summary>
/// <remarks>
/// The report writes itself when Review is opened, so nothing else stands between a stray click
/// and a paid call. Two conditions have to hold, and both are about having something to say:
/// the topic must clear <see cref="SkillConfidence"/>'s threshold, and enough new problems must
/// have been worked since the last report to change what it would say.
///
/// The second condition is what makes the page free to open. Without it, every visit rewrites
/// the same paragraphs from the same numbers, and the cost scales with how often the student
/// looks rather than how much they have done.
/// </remarks>
public static class SkillReportGate
{
    /// <summary>
    /// Problems that must be worked before a standing report is rewritten. One more attempt
    /// moves almost nothing, and rewriting for it would spend a call to change a sentence.
    /// </summary>
    public const int MinimumNewAttempts = 5;

    public static ReportDecision Decide(TopicConfidence topic, SkillReportRecord? existing)
    {
        if (!topic.HasEnoughData)
        {
            return ReportDecision.No(
                $"needs {SkillConfidence.MinimumAttempts} problems across "
                + $"{SkillConfidence.MinimumJudgedSkills} skills; so far {topic.TotalAttempts} "
                + $"across {topic.Skills.Count}");
        }

        if (existing is null)
        {
            return ReportDecision.Yes;
        }

        var since = topic.TotalAttempts - existing.AttemptsAtGeneration;
        return since >= MinimumNewAttempts
            ? ReportDecision.Yes
            : ReportDecision.No($"{since} new problems since the last report; {MinimumNewAttempts} rewrites it");
    }
}
