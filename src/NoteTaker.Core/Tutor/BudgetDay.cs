namespace NoteTaker.Core.Tutor;

/// <summary>
/// When a budget day and a budget month begin.
/// </summary>
/// <remarks>
/// Pacific, always — not the machine's local zone and not UTC. UTC put the boundary at 5pm the
/// previous afternoon here, so an evening session opened the next morning already part-spent.
/// Local time fixed that but moves the goalposts the moment the laptop crosses a timezone or its
/// clock drifts: the same session would be charged to two different days depending on where it
/// was opened. Pinning the ledger to one zone makes "today" mean one thing everywhere.
///
/// The instant itself should come from <c>IClock</c>, which the app can correct against a time
/// server. This type only decides which day an instant falls in.
/// </remarks>
public static class BudgetDay
{
    /// <summary>The zone the budget is kept in.</summary>
    /// <remarks>
    /// Looked up by both the Windows and the IANA id: the same zone is called "Pacific Standard
    /// Time" on Windows and "America/Los_Angeles" elsewhere, and .NET only accepts its host's
    /// spelling. Falling back to the machine's own zone is better than throwing — a budget in the
    /// wrong zone still works; one that crashes the tutor does not.
    /// </remarks>
    public static readonly TimeZoneInfo Zone = FindZone();

    private static TimeZoneInfo FindZone()
    {
        foreach (var id in new[] { "Pacific Standard Time", "America/Los_Angeles" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                // Try the other spelling.
            }
            catch (InvalidTimeZoneException)
            {
                // Corrupt zone data; fall through to local.
            }
        }

        return TimeZoneInfo.Local;
    }

    /// <summary>The instant the budget day containing <paramref name="instant"/> began.</summary>
    public static DateTimeOffset StartOf(DateTimeOffset instant)
    {
        var here = TimeZoneInfo.ConvertTime(instant, Zone);
        var midnight = here.Date;

        // The offset in effect AT midnight, not the one in effect now: on the two days a year the
        // clocks move, those differ, and using "now" would place the boundary an hour out.
        return new DateTimeOffset(midnight, Zone.GetUtcOffset(midnight));
    }

    /// <summary>The instant the budget month containing <paramref name="instant"/> began.</summary>
    public static DateTimeOffset StartOfMonth(DateTimeOffset instant)
    {
        var here = TimeZoneInfo.ConvertTime(instant, Zone);
        var first = new DateTime(here.Year, here.Month, 1);
        return new DateTimeOffset(first, Zone.GetUtcOffset(first));
    }

    /// <summary>How many days the month containing <paramref name="instant"/> has.</summary>
    /// <remarks>
    /// The divisor behind the flat share. February and August differ by three days, and dividing
    /// by the real length is what keeps "eight dollars a month" true in both.
    /// </remarks>
    public static int DaysInMonth(DateTimeOffset instant)
    {
        var here = TimeZoneInfo.ConvertTime(instant, Zone);
        return DateTime.DaysInMonth(here.Year, here.Month);
    }

    /// <summary>Days left in the month, counting the one <paramref name="instant"/> falls in.</summary>
    public static int DaysLeftInMonth(DateTimeOffset instant)
    {
        var here = TimeZoneInfo.ConvertTime(instant, Zone);
        return DateTime.DaysInMonth(here.Year, here.Month) - here.Day + 1;
    }
}
