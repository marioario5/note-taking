using NoteTaker.Core.Models;

namespace NoteTaker.Core.Tutor;

/// <summary>
/// Turns a topic's <see cref="SkillEvent"/> history into per-skill confidence, ranked weakest
/// first. Pure in-memory arithmetic over rows already on disk: opening Review costs nothing.
/// </summary>
/// <remarks>
/// What is being measured is independence, not correctness. Working with a tutor, every problem
/// ends right sooner or later, so "did it come out right" carries almost no information — the
/// question worth answering is how much help it took, and whether that is shrinking. So the
/// score is built from <see cref="SkillAttempt.Independence"/>, weighted toward recent attempts,
/// and <see cref="SkillTrend"/> reports the direction separately.
/// </remarks>
public static class SkillConfidence
{
    /// <summary>
    /// Attempts a topic needs before Review will draw anything. Deliberately counted in
    /// attempts, not verdicts: fifteen verdicts from two problems is what made an earlier
    /// version show three confident-looking meters on a first sitting.
    /// </summary>
    public const int MinimumAttempts = 5;

    /// <summary>
    /// And across at least this many skills, so one skill drilled repeatedly does not stand in
    /// for knowing the topic.
    /// </summary>
    public const int MinimumJudgedSkills = 3;

    /// <summary>Attempts needed before earlier and later halves can be compared at all.</summary>
    private const int MinimumAttemptsForTrend = 4;

    /// <summary>
    /// How much less each older attempt counts than the one after it. Recent work should
    /// dominate — that is what makes the meter track improvement rather than average it away —
    /// but not so steeply that a single bad problem erases a month of good ones.
    /// </summary>
    private const double RecencyFalloff = 0.7;

    /// <summary>Difference in mean independence between halves that counts as a real move.</summary>
    private const double TrendThreshold = 0.12;

    /// <summary>
    /// How long a score takes to travel half the distance back to neutral. Roughly a month:
    /// short enough that a semester's neglect is visible, long enough that a week off is not.
    /// </summary>
    private const double DecayHalfLifeDays = 30.0;

    private const double Neutral = 0.5;

    public static TopicConfidence Score(IReadOnlyList<SkillEvent> events, DateTimeOffset now)
    {
        var attempts = SkillAttempts.Segment(events);
        if (attempts.Count == 0)
        {
            return new TopicConfidence([], 0, HasEnoughData: false, Confidence: Neutral, LastPractised: null);
        }

        var skills = attempts
            .GroupBy(a => a.Skill, StringComparer.OrdinalIgnoreCase)
            .Select(group => ScoreOne(group.Key, group.OrderBy(a => a.EndedAt).ToList(), now))
            .OrderBy(score => score.Confidence)
            .ThenByDescending(score => score.Corrections)
            .ThenBy(score => score.Skill, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new TopicConfidence(
            skills,
            attempts.Count,
            HasEnoughData: attempts.Count >= MinimumAttempts && skills.Count >= MinimumJudgedSkills,
            Confidence: skills.Average(s => s.Confidence),
            LastPractised: events.Max(e => e.CreatedAt));
    }

    private static SkillScore ScoreOne(string skill, IReadOnlyList<SkillAttempt> ordered, DateTimeOffset now)
    {
        // Newest attempt carries full weight; each older one carries less.
        double weighted = 0;
        double weights = 0;
        var weight = 1.0;

        for (var i = ordered.Count - 1; i >= 0; i--)
        {
            weighted += ordered[i].Independence * weight;
            weights += weight;
            weight *= RecencyFalloff;
        }

        var observed = weighted / weights;

        // Decay moves the score toward neutral rather than downward. Time passing is a loss of
        // evidence, not evidence of a loss — so a stale strength drifts back into view for
        // revisiting, and a stale weakness loses certainty without being quietly forgiven.
        var lastPractised = ordered[^1].EndedAt;
        var days = Math.Max(0.0, (now - lastPractised).TotalDays);
        var retained = Math.Pow(0.5, days / DecayHalfLifeDays);

        return new SkillScore(
            skill,
            ordered.Count,
            ordered.Sum(a => a.Corrections),
            Confidence: Neutral + ((observed - Neutral) * retained),
            Trend: TrendOf(ordered),
            LastPractised: lastPractised,
            DaysSinceLastPractised: (int)days);
    }

    /// <summary>
    /// Compares the earlier half of the attempts against the later half. Halves rather than a
    /// fitted slope because the counts here are small and a slope over four points reads noise
    /// as direction.
    /// </summary>
    private static SkillTrend TrendOf(IReadOnlyList<SkillAttempt> ordered)
    {
        if (ordered.Count < MinimumAttemptsForTrend)
        {
            return SkillTrend.Unknown;
        }

        var split = ordered.Count / 2;
        var earlier = ordered.Take(split).Average(a => a.Independence);
        var later = ordered.Skip(ordered.Count - split).Average(a => a.Independence);
        var move = later - earlier;

        if (move >= TrendThreshold)
        {
            return SkillTrend.Improving;
        }

        return move <= -TrendThreshold ? SkillTrend.Slipping : SkillTrend.Steady;
    }
}
