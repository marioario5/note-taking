namespace NoteTaker.Core.Models;

public sealed class ApiUsageLog
{
    public long Id { get; set; }
    public TutorCallType CallType { get; set; }
    public string Model { get; set; } = string.Empty;
    public int TokensIn { get; set; }
    public int TokensOut { get; set; }

    /// <summary>Of TokensIn, how many were served from a prompt cache (billed at a fraction of the normal rate).</summary>
    public int TokensCached { get; set; }

    /// <summary>Of TokensIn, how many wrote a new cache entry this call (a one-time premium, then cheap on reuse).</summary>
    public int TokensCacheWrite { get; set; }

    public decimal CostEstimate { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record UsageSummary(int Calls, int TokensIn, int TokensOut, decimal Cost, int CachedIn = 0)
{
    public static readonly UsageSummary Empty = new(0, 0, 0, 0m);
}

/// <summary>
/// One (call type, model) line of spend. Exists so the usage panel can answer "where is it
/// going" instead of just "how much" — the single number that shipped before this could not
/// have told anyone that a heavy day's $0.299 was 82% one call type on one model.
/// </summary>
public sealed record UsageBreakdownRow(
    TutorCallType CallType,
    string Model,
    int Calls,
    int TokensIn,
    int TokensOut,
    int TokensCached,
    decimal Cost);
