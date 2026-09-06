using System;
using System.Windows.Media;
using NoteTaker.Core.Models;

namespace NoteTaker.App.ViewModels;

/// <summary>
/// One row of the Review meter list. Presentation only: every number here comes straight from
/// <see cref="NoteTaker.Core.Tutor.SkillConfidence"/>, which is where the arithmetic is tested.
/// </summary>
public sealed class SkillScoreViewModel(SkillScore score)
{
    private static readonly Brush WeakBrush =
        new SolidColorBrush(Color.FromRgb(0xE8, 0x8F, 0x74));

    private static readonly Brush MiddlingBrush =
        new SolidColorBrush(Color.FromRgb(0xFE, 0xBB, 0x55));

    private static readonly Brush StrongBrush =
        new SolidColorBrush(Color.FromRgb(0x6F, 0x9B, 0x7D));

    private static readonly Brush MutedBrush =
        new SolidColorBrush(Color.FromRgb(0xA9, 0xB7, 0xDE));

    public string Skill { get; } = score.Skill;

    /// <summary>
    /// Problems attempted and how much help they took. Both are shown because the bar cannot
    /// separate "three problems, unaided" from "three problems, walked through every line" —
    /// and with a tutor, both end up correct.
    /// </summary>
    public string Counts { get; } = score.Corrections == 0
        ? $"{Plural(score.Attempts)} · unaided"
        : $"{Plural(score.Attempts)} · {score.Corrections} correction{(score.Corrections == 1 ? "" : "s")}";

    /// <summary>
    /// Decay is meant to be visible rather than a silent adjustment to the bar, so the reason a
    /// score has drifted is written next to it.
    /// </summary>
    public string Recency { get; } = score.DaysSinceLastPractised switch
    {
        0 => "today",
        1 => "yesterday",
        < 7 => $"{score.DaysSinceLastPractised} days ago",
        < 14 => "last week",
        < 60 => $"{score.DaysSinceLastPractised / 7} weeks ago",
        _ => $"{score.DaysSinceLastPractised / 30} months ago",
    };

    /// <summary>
    /// Direction matters more than level here: a weak skill that is climbing needs different
    /// study from a weak skill that is not. Blank below four attempts, where two halves of two
    /// would be comparing noise.
    /// </summary>
    public string TrendLabel { get; } = score.Trend switch
    {
        SkillTrend.Improving => "improving",
        SkillTrend.Slipping => "slipping",
        SkillTrend.Steady => "steady",
        _ => string.Empty,
    };

    public Brush TrendBrush { get; } = score.Trend switch
    {
        SkillTrend.Improving => StrongBrush,
        SkillTrend.Slipping => WeakBrush,
        _ => MutedBrush,
    };

    /// <summary>Bar width as a fraction of the track.</summary>
    public double Fill { get; } = Math.Clamp(score.Confidence, 0.02, 1.0);

    public Brush MeterBrush { get; } = score.Confidence switch
    {
        < 0.45 => WeakBrush,
        < 0.62 => MiddlingBrush,
        _ => StrongBrush,
    };

    private static string Plural(int attempts) =>
        attempts == 1 ? "1 problem" : $"{attempts} problems";
}
