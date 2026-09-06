using NoteTaker.Core.Abstractions;
using NoteTaker.Core.Models;

namespace NoteTaker.Core.Tutor;

/// <summary>Why a call was refused, when it was.</summary>
public enum BudgetLimit
{
    None,

    /// <summary>Live checks only: the per-hour ceiling.</summary>
    LiveRate,

    /// <summary>The day's own ceiling. Recoverable — tomorrow raises it, or the student borrows.</summary>
    Daily,

    /// <summary>The month's cap. Nothing raises this but a new month or a new setting.</summary>
    Monthly,
}

public readonly record struct BudgetDecision(bool Allowed, string? Reason, BudgetLimit Limit = BudgetLimit.None)
{
    public static readonly BudgetDecision Ok = new(true, null);
    public static BudgetDecision Deny(string reason, BudgetLimit limit) => new(false, reason, limit);
}

/// <summary>Where today stands, for the usage panel and the borrow dialog.</summary>
public readonly record struct BudgetSnapshot(
    decimal SpentToday,
    decimal CeilingToday,
    decimal SpentThisMonth,
    decimal MonthlyCap,
    int DaysLeft)
{
    public decimal RemainingToday => Math.Max(0m, CeilingToday - SpentToday);

    public decimal RemainingThisMonth => Math.Max(0m, MonthlyCap - SpentThisMonth);
}

/// <summary>
/// Keeps spending predictable: an hourly ceiling on live checks, a hard daily dollar ceiling
/// derived from the month, and the month's own cap behind both.
/// </summary>
public sealed class TutorBudget(IUsageRepository usage, IClock clock, TutorOptions options)
{
    private readonly Queue<DateTimeOffset> _liveCalls = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _borrowedDay;
    private decimal _borrowed;

    public async Task<BudgetDecision> CheckAsync(TutorCallType callType, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = clock.UtcNow;

            if (callType == TutorCallType.LiveCheck)
            {
                TrimLiveWindow(now);
                if (_liveCalls.Count >= options.MaxLiveCallsPerHour)
                {
                    return BudgetDecision.Deny(
                        $"Live checks paused: {options.MaxLiveCallsPerHour}/hour limit reached.",
                        BudgetLimit.LiveRate);
                }
            }

            var snapshot = await SnapshotAsync(now, ct).ConfigureAwait(false);

            if (snapshot.RemainingThisMonth <= 0)
            {
                return BudgetDecision.Deny(
                    $"Monthly budget reached (${snapshot.SpentThisMonth:0.00} of ${options.MonthlyCostCapUsd:0.00}). "
                    + "It resets on the 1st, Pacific.",
                    BudgetLimit.Monthly);
            }

            // A hard stop, not a soft one. The day's ceiling already grew to absorb every day
            // that went unused, so reaching it means today really has had its turn.
            if (snapshot.RemainingToday <= 0)
            {
                return BudgetDecision.Deny(
                    $"Today's limit is spent (${snapshot.SpentToday:0.00} of ${snapshot.CeilingToday:0.00}). "
                    + $"${snapshot.RemainingThisMonth:0.00} is left for the other {snapshot.DaysLeft - 1} day(s) "
                    + "of the month.",
                    BudgetLimit.Daily);
            }

            return BudgetDecision.Ok;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Today's ceiling and what has gone against it, including any borrow granted.</summary>
    public async Task<BudgetSnapshot> SnapshotAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var month = await usage.GetSummaryAsync(BudgetDay.StartOfMonth(now), ct).ConfigureAwait(false);
        var today = await usage.GetSummaryAsync(BudgetDay.StartOf(now), ct).ConfigureAwait(false);
        var daysLeft = BudgetDay.DaysLeftInMonth(now);

        return new BudgetSnapshot(
            today.Cost,
            MonthlyBudget.CeilingToday(
                options.MonthlyCostCapUsd,
                month.Cost,
                today.Cost,
                daysLeft,
                options.MinimumDailyAllowanceUsd,
                BorrowedToday(now)),
            month.Cost,
            options.MonthlyCostCapUsd,
            daysLeft);
    }

    /// <summary>
    /// Raises today's ceiling by <paramref name="amount"/>, after the student has said yes to a
    /// dialog spelling out what it does to the rest of the month.
    /// </summary>
    /// <remarks>
    /// Held in memory and tied to the day it was granted for: a borrow is a decision about one
    /// afternoon, and it should not survive a restart, a night's sleep, or the month's rollover.
    /// The month's cap still bounds it — that one is never negotiable here.
    /// </remarks>
    public void GrantBorrow(decimal amount)
    {
        if (amount <= 0)
        {
            return;
        }

        var day = BudgetDay.StartOf(clock.UtcNow);

        // A borrow granted yesterday is not carried in: the day it belonged to is over.
        _borrowed = _borrowedDay == day ? _borrowed + amount : amount;
        _borrowedDay = day;
    }

    private decimal BorrowedToday(DateTimeOffset now)
        => _borrowedDay == BudgetDay.StartOf(now) ? _borrowed : 0m;

    public void RecordLiveCall()
    {
        lock (_liveCalls)
        {
            _liveCalls.Enqueue(clock.UtcNow);
        }
    }

    private void TrimLiveWindow(DateTimeOffset now)
    {
        lock (_liveCalls)
        {
            while (_liveCalls.Count > 0 && now - _liveCalls.Peek() > TimeSpan.FromHours(1))
            {
                _liveCalls.Dequeue();
            }
        }
    }
}
