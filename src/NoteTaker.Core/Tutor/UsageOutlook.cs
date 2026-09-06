using NoteTaker.Core.Models;

namespace NoteTaker.Core.Tutor;

/// <summary>One day of the week strip: what it was allowed, and what it actually cost.</summary>
/// <param name="Allowance">
/// What that day was actually allowed, replayed from the spending that came before it. A quiet
/// day raises the days after it and a heavy one lowers them, so this rises and falls across the
/// week — which is the movement the whole screen exists to show.
/// </param>
public readonly record struct DaySpend(DateTimeOffset Day, SpendSplit Spent, decimal Allowance);

/// <summary>
/// A day's spending broken up by what asked for it.
/// </summary>
/// <remarks>
/// Three groups, because they are three different decisions. <paramref name="Tutor"/> is the
/// conversation — the part that grows when you ask more. <paramref name="Review"/> is marking and
/// written reports, which you choose deliberately and rarely. <paramref name="Other"/> is
/// everything the app did on its own.
///
/// Grouped from the call type already on every logged row, so the split costs nothing: it is a
/// second reading of numbers the ledger has always held.
/// </remarks>
public readonly record struct SpendSplit(decimal Tutor, decimal Review, decimal Other)
{
    public decimal Total => Tutor + Review + Other;

    /// <summary>Files one call's cost under the group its type belongs to.</summary>
    public SpendSplit Add(TutorCallType callType, decimal cost) => callType switch
    {
        TutorCallType.SocraticChat => this with { Tutor = Tutor + cost },

        // Marking a page and writing a report are the same activity from the student's side:
        // banking what they did so Review can read it back.
        TutorCallType.SkillCheck or TutorCallType.SkillReport
            => this with { Review = Review + cost },

        _ => this with { Other = Other + cost },
    };
}

/// <summary>
/// Where the month's money has gone and where the rest of it is going.
/// </summary>
/// <remarks>
/// The usage screen used to be a wall of token counts, which answers "what happened" and not the
/// question actually being asked: am I going to run out. Three things answer that — what each day
/// cost against what it was allowed, how the cap is spread over the days still to come, and
/// whether the current rate lands under or over the cap by the month's end.
///
/// All of it is arithmetic over rows already on disk, so the screen costs nothing to open.
/// </remarks>
public sealed record UsageOutlook(
    IReadOnlyList<DaySpend> Week,
    decimal MonthlyCap,
    decimal SpentThisMonth,
    decimal EvenShare,
    decimal AllowanceToday,
    decimal ProjectedMonth,
    int DaysElapsed,
    int DaysLeft,
    decimal ChatTurnCost)
{
    /// <summary>What the month has left.</summary>
    public decimal RemainingThisMonth => Math.Max(0m, MonthlyCap - SpentThisMonth);

    /// <summary>Spend per day so far, which is what the projection extends.</summary>
    public decimal SpentPerDay => DaysElapsed <= 0 ? 0m : SpentThisMonth / DaysElapsed;

    /// <summary>
    /// How the projection lands against the cap: over 1 is heading past it, under 1 is under.
    /// </summary>
    public double ProjectedFraction =>
        MonthlyCap <= 0 ? 0 : (double)(ProjectedMonth / MonthlyCap);

    /// <summary>
    /// How today's allowance compares with the flat share the month started on. Above 1 means
    /// the quiet days have paid forward; below 1 means the heavy ones are being paid for.
    /// </summary>
    public double ShareAgainstEven =>
        EvenShare <= 0 ? 1 : (double)(AllowanceToday / EvenShare);

    /// <summary>How much of the month's cap is gone, 0 to 1.</summary>
    public double MonthUsed => MonthlyCap <= 0 ? 0 : (double)(SpentThisMonth / MonthlyCap);

    /// <summary>How much of today's own allowance is gone, 0 to 1 and beyond.</summary>
    public double TodayUsed => AllowanceToday <= 0 ? 1 : (double)(SpentToday / AllowanceToday);

    /// <summary>What today has spent so far.</summary>
    public decimal SpentToday => Week.Count == 0 ? 0m : Week[^1].Spent.Total;

    /// <summary>
    /// How many more questions a day today's raised cap buys over the flat share the month
    /// opened on, or null with no measured price for a question. Negative when the cap has
    /// fallen, which is the same number read the other way.
    /// </summary>
    /// <remarks>
    /// The percentage above it says the cap moved; this says what the movement is worth. "8%
    /// more" is not something anyone can act on — "two more questions today" is.
    /// </remarks>
    public int? ExtraQuestionsPerDay =>
        ChatTurnCost <= 0 ? null : (int)Math.Round((AllowanceToday - EvenShare) / ChatTurnCost);

    /// <summary>
    /// Roughly how many tutor questions a day the rest of the month affords, or null when
    /// nothing has been asked yet and there is no measured price for one.
    /// </summary>
    /// <remarks>
    /// Priced from this month's own chat turns rather than a constant, because what a turn costs
    /// moves with the page image, the length of the thread and the model. Deliberately the
    /// coarsest number on the screen: it answers "can I afford to keep working" and nothing
    /// finer, so it is shown rounded and hedged.
    /// </remarks>
    public int? QuestionsPerDayLeft
    {
        get
        {
            if (ChatTurnCost <= 0 || DaysLeft <= 0)
            {
                return null;
            }

            return (int)Math.Floor(RemainingThisMonth / DaysLeft / ChatTurnCost);
        }
    }

    /// <summary>
    /// Builds the outlook from a month's daily spend.
    /// </summary>
    /// <param name="dailySpend">
    /// Cost per calendar day, keyed by the day's Pacific midnight. Days with no spend may be
    /// absent; they are read as zero.
    /// </param>
    public static UsageOutlook Build(
        decimal monthlyCap,
        IReadOnlyDictionary<DateTimeOffset, SpendSplit> dailySpend,
        DateTimeOffset now,
        decimal chatTurnCost = 0m,
        int weekLength = 7)
    {
        var today = BudgetDay.StartOf(now);
        var monthStart = BudgetDay.StartOfMonth(now);
        var daysLeft = BudgetDay.DaysLeftInMonth(now);
        var daysInMonth = DateTime.DaysInMonth(
            TimeZoneInfo.ConvertTime(now, BudgetDay.Zone).Year,
            TimeZoneInfo.ConvertTime(now, BudgetDay.Zone).Month);
        var daysElapsed = daysInMonth - daysLeft + 1;

        var spentThisMonth = dailySpend
            .Where(pair => pair.Key >= monthStart)
            .Sum(pair => pair.Value.Total);

        // The flat share the month opened on, before any redistribution. It is the reference the
        // whole screen is read against: every other number says how far from it things have got.
        var evenShare = daysInMonth <= 0 ? 0m : monthlyCap / daysInMonth;

        var spentBeforeToday = spentThisMonth - Spent(dailySpend, today).Total;
        var allowanceToday = MonthlyBudget.AllowanceToday(monthlyCap, spentBeforeToday, daysLeft);

        // The strip stops at the first of the month. A day in the previous month is under a
        // different cap that has already been spent or forgiven, and drawing it here wrecked
        // both the meaning and the scale: on 5 September the 30th and 31st of August showed
        // allowances of $4.00 and $8.00 — the whole August cap divided by its last two days —
        // which flattened every day of the current month into an unreadable sliver.
        var days = Math.Min(weekLength, daysElapsed);
        var week = new List<DaySpend>(days);
        for (var back = days - 1; back >= 0; back--)
        {
            var day = BudgetDay.StartOf(today.AddDays(-back));
            week.Add(new DaySpend(day, Spent(dailySpend, day), AllowanceOn(monthlyCap, dailySpend, day)));
        }

        return new UsageOutlook(
            week,
            monthlyCap,
            spentThisMonth,
            evenShare,
            allowanceToday,
            daysElapsed <= 0 ? 0m : spentThisMonth / daysElapsed * daysInMonth,
            daysElapsed,
            daysLeft,
            chatTurnCost);
    }

    /// <summary>
    /// What one day was allowed, replayed from everything its own month spent before it.
    /// </summary>
    /// <remarks>
    /// Exact, not an estimate. The allowance is a pure function of the cap, what the month had
    /// already spent when the day opened, and how many days it still had to cover — and all
    /// three are known for any past day, so the rise and fall can be drawn as it happened.
    ///
    /// Each day is replayed against its OWN month, so a strip spanning the first of the month
    /// shows the reset rather than smearing one month's spending across the other's cap.
    /// </remarks>
    private static decimal AllowanceOn(
        decimal monthlyCap,
        IReadOnlyDictionary<DateTimeOffset, SpendSplit> spend,
        DateTimeOffset day)
    {
        var local = TimeZoneInfo.ConvertTime(day, BudgetDay.Zone);
        var monthStart = BudgetDay.StartOfMonth(day);
        var daysLeft = DateTime.DaysInMonth(local.Year, local.Month) - local.Day + 1;

        var spentBefore = spend
            .Where(pair => pair.Key >= monthStart && pair.Key < day)
            .Sum(pair => pair.Value.Total);

        return MonthlyBudget.AllowanceToday(monthlyCap, spentBefore, daysLeft);
    }

    private static SpendSplit Spent(IReadOnlyDictionary<DateTimeOffset, SpendSplit> spend, DateTimeOffset day)
        => spend.TryGetValue(day, out var value) ? value : default;
}
