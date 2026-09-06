namespace NoteTaker.Core.Tutor;

/// <summary>
/// Today's spending allowance, worked out from a monthly cap and what is left of the month.
/// </summary>
/// <remarks>
/// A fixed daily cap wastes a month. Study is lumpy — nothing on Tuesday, four hours on Sunday —
/// and a flat allowance both strands Tuesday's money and cuts Sunday short while the month as a
/// whole is underspent.
///
/// So the allowance is recomputed each day from what actually remains:
///
///     today = (monthly cap − spent this month) ÷ days left in the month, today included
///
/// Skip a day and tomorrow's share rises on its own; overspend and the rest of the month tightens
/// to pay for it. No carry-over ledger to keep, because the remaining money and the remaining days
/// are both facts the database already knows.
///
/// The month is the only hard boundary: whatever the daily arithmetic says, spending stops at the
/// cap. February and August differ by three days, and dividing by the real length is what keeps
/// the promise "eight dollars a month" true in both.
/// </remarks>
public static class MonthlyBudget
{
    /// <summary>What today may spend, in dollars.</summary>
    /// <param name="monthlyCap">The whole month's ceiling.</param>
    /// <param name="spentThisMonth">Everything billed since the month began, today included.</param>
    /// <param name="daysLeft">Days remaining in the month, counting today.</param>
    public static decimal AllowanceToday(decimal monthlyCap, decimal spentThisMonth, int daysLeft)
    {
        var remaining = monthlyCap - spentThisMonth;
        if (remaining <= 0 || daysLeft <= 0)
        {
            return 0m;
        }

        return remaining / daysLeft;
    }

    /// <summary>
    /// The most today is allowed to spend in total — a hard ceiling, not a target.
    /// </summary>
    /// <remarks>
    /// Redistribution only ever works in one direction on its own: unused days raise the days
    /// after them. The reverse — a single afternoon quietly eating a week — is what this ceiling
    /// exists to stop, because that is the failure the student notices, and noticing it a week
    /// later is too late to do anything about.
    ///
    /// <paramref name="minimumAllowance"/> is a floor under the share, so a nearly-spent month
    /// still answers a question or two instead of going silent for a fortnight. It is a floor,
    /// never an overrun: the month's own remaining money still bounds it from above.
    ///
    /// Going past this is possible, but only as something the student chooses in front of a
    /// dialog that says what it costs — see <paramref name="borrowed"/>.
    /// </remarks>
    /// <param name="borrowed">
    /// Extra granted for today after an explicit, informed confirmation. Zero by default.
    /// </param>
    public static decimal CeilingToday(
        decimal monthlyCap,
        decimal spentThisMonth,
        decimal spentToday,
        int daysLeft,
        decimal minimumAllowance = 0m,
        decimal borrowed = 0m)
    {
        var spentBefore = spentThisMonth - spentToday;
        var share = AllowanceToday(monthlyCap, spentBefore, daysLeft);
        var monthLeft = Math.Max(0m, monthlyCap - spentBefore);

        return Math.Min(monthLeft, Math.Max(share, minimumAllowance) + borrowed);
    }

    /// <summary>
    /// What today may still spend: its ceiling, less what today has already used.
    /// </summary>
    /// <remarks>
    /// Today's spending sits in <paramref name="spentThisMonth"/> as well, so the ceiling is
    /// computed as if today had not started. Subtracting today's own outlay afterwards is what
    /// turns a ceiling into a remaining balance.
    /// </remarks>
    public static decimal RemainingToday(
        decimal monthlyCap,
        decimal spentThisMonth,
        decimal spentToday,
        int daysLeft,
        decimal minimumAllowance = 0m,
        decimal borrowed = 0m)
    {
        var ceiling = CeilingToday(
            monthlyCap, spentThisMonth, spentToday, daysLeft, minimumAllowance, borrowed);

        return Math.Max(0m, ceiling - spentToday);
    }

    /// <summary>
    /// What each of the remaining days gets if today stops now, and if today spends
    /// <paramref name="extra"/> more. The two numbers a borrow dialog has to show.
    /// </summary>
    public static (decimal IfYouStop, decimal IfYouBorrow) DaysAfterToday(
        decimal monthlyCap,
        decimal spentThisMonth,
        decimal extra,
        int daysLeft)
    {
        var after = daysLeft - 1;
        return (
            AllowanceToday(monthlyCap, spentThisMonth, after),
            AllowanceToday(monthlyCap, spentThisMonth + extra, after));
    }
}
