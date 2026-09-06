using NoteTaker.Core.Models;

namespace NoteTaker.Core.Tutor;

/// <summary>
/// Groups a page's full mistake history (active and dismissed findings alike — see
/// <see cref="Abstractions.ITutorRepository.GetAllFeedbackForPageAsync"/>) into recurring
/// topics, so Review mode can target them without spending a single token: this is pure
/// in-memory grouping over data already sitting in the database.
/// </summary>
public static class WeaknessAggregator
{
    private const int MinimumRecurrence = 2;

    /// <summary>
    /// Ranks topics by how often they recur (ties broken by worst severity, then most
    /// recent), keeping only topics that showed up at least twice — a single slip isn't a
    /// weakness, it's just a slip.
    /// </summary>
    public static IReadOnlyList<WeaknessTopic> Rank(IReadOnlyList<TutorFeedback> history, int top = 3)
    {
        if (history.Count == 0)
        {
            return [];
        }

        return history
            .Where(f => !string.IsNullOrWhiteSpace(f.Topic))
            .GroupBy(f => f.Topic!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var ordered = group.OrderByDescending(f => f.CreatedAt).ToList();
                return new WeaknessTopic(
                    Topic: ordered[0].Topic!.Trim(),
                    Count: ordered.Count,
                    MaxSeverity: ordered.Max(f => f.Severity),
                    SampleLabel: ordered[0].Label);
            })
            .Where(topic => topic.Count >= MinimumRecurrence)
            .OrderByDescending(topic => topic.Count)
            .ThenByDescending(topic => topic.MaxSeverity)
            .Take(top)
            .ToList();
    }
}
