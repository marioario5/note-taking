using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NoteTaker.Core.Tutor;

namespace NoteTaker.App.Views;

/// <summary>
/// The week's spending against the day-by-day cap, then how the month's cap is being spread.
/// </summary>
/// <remarks>
/// The chart is two bars a day, not one: a wide pale bar for what that day was allowed, and a
/// narrow solid bar in front of it for what it actually cost. That pairing is the whole point of
/// the redistributing cap — the pale bar rises after a quiet day and falls after a heavy one, so
/// a week of it shows the mechanism working without a word of explanation.
///
/// An earlier version drew one bar a day against a flat line and coloured the overspent days
/// red. It hid the very thing it was built to show: the line never moved, so the cap looked
/// fixed, and the colour was carrying information the shape should have carried.
/// </remarks>
public sealed class UsageBoard : StackPanel
{
    private const double ChartHeight = 150;

    public UsageBoard(UsageOutlook outlook)
    {
        Children.Add(Label(outlook.Week.Count >= 7 ? "THIS WEEK" : "THIS MONTH SO FAR"));
        Children.Add(Caption("Pale is the day's cap, solid is what it cost."));
        Children.Add(BuildKey());
        Children.Add(BuildChart(outlook));
        Children.Add(BuildStats(outlook));
        Children.Add(BuildDistribution(outlook));
    }

    private UIElement BuildChart(UsageOutlook outlook)
    {
        // One scale for both bars, so a spend bar taller than its own halo is exactly the days
        // that went over. Scaling them separately would make that impossible to see.
        var ceiling = outlook.Week.Count == 0
            ? 0.01m
            : Math.Max(outlook.Week.Max(d => d.Spent.Total), outlook.Week.Max(d => d.Allowance));

        if (ceiling <= 0)
        {
            ceiling = 0.01m;
        }

        var chart = new Grid { Height = ChartHeight, Margin = new Thickness(0, 14, 0, 0) };
        var labels = new Grid { Margin = new Thickness(0, 7, 0, 0) };

        var today = outlook.Week.Count == 0 ? default : outlook.Week[^1].Day;

        for (var i = 0; i < outlook.Week.Count; i++)
        {
            chart.ColumnDefinitions.Add(new ColumnDefinition());
            labels.ColumnDefinitions.Add(new ColumnDefinition());

            var day = outlook.Week[i];

            var cap = new Border
            {
                Height = Math.Max(2, ChartHeight * (double)(day.Allowance / ceiling)),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(4, 0, 4, 0),
                CornerRadius = new CornerRadius(4, 4, 0, 0),
                Background = (Brush)FindResource("AccentTint"),
            };

            // Stacked by what asked for the money, tutor at the bottom. Colour carries the
            // group and nothing else — never the day, never whether it went over — so the same
            // hue always means the same thing wherever it appears on the chart.
            var spent = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                Width = 18,
            };

            Segment(spent, day.Spent.Other, ceiling, "TextFaint");
            Segment(spent, day.Spent.Review, ceiling, "Success");
            Segment(spent, day.Spent.Tutor, ceiling, "Accent");

            Grid.SetColumn(cap, i);
            Grid.SetColumn(spent, i);
            chart.Children.Add(cap);
            chart.Children.Add(spent);

            // A transparent panel over the whole column, added last so it sits above the bars.
            // Hovering a bar means hovering the day, and on a quiet day the bar is two pixels
            // tall — hanging the tooltip on the bar itself would make the days worth asking
            // about the hardest ones to hit. Transparent rather than unset: a null background
            // is not hit-testable in WPF, so the panel would be invisible to the mouse too.
            var hover = new Border { Background = Brushes.Transparent, ToolTip = DayTip(day) };
            Grid.SetColumn(hover, i);
            chart.Children.Add(hover);

            var caption = new TextBlock
            {
                Text = TimeZoneInfo.ConvertTime(day.Day, BudgetDay.Zone).ToString("ddd")[..1],
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 11,
                Foreground = (Brush)FindResource(day.Day == today ? "TextBody" : "TextFaint"),
            };

            Grid.SetColumn(caption, i);
            labels.Children.Add(caption);
        }

        var wrap = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        wrap.Children.Add(chart);
        wrap.Children.Add(labels);
        return wrap;
    }

    /// <summary>
    /// What one day cost, said as a share of that day's own cap.
    /// </summary>
    /// <remarks>
    /// Against its OWN cap, not the flat share and not the month: the cap moved that day, and a
    /// percentage measured against anything else answers a question nobody asked while looking
    /// at that bar.
    /// </remarks>
    private UIElement DayTip(DaySpend day)
    {
        var tip = new StackPanel { MaxWidth = 230 };

        tip.Children.Add(new TextBlock
        {
            Text = TimeZoneInfo.ConvertTime(day.Day, BudgetDay.Zone).ToString("dddd d MMMM"),
            FontWeight = FontWeights.SemiBold,
        });

        var used = day.Allowance <= 0 ? 0 : (double)(day.Spent.Total / day.Allowance);
        tip.Children.Add(new TextBlock
        {
            Text = $"{used * 100:0}% of the day's ${day.Allowance:0.00} — ${day.Spent.Total:0.0000} spent",
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

        // Only the groups that actually spent something. A row of "$0.00" for every group a day
        // never touched is three lines saying nothing.
        var parts = new List<string>();
        if (day.Spent.Tutor > 0)
        {
            parts.Add($"tutor ${day.Spent.Tutor:0.0000}");
        }

        if (day.Spent.Review > 0)
        {
            parts.Add($"review ${day.Spent.Review:0.0000}");
        }

        if (day.Spent.Other > 0)
        {
            parts.Add($"other ${day.Spent.Other:0.0000}");
        }

        if (parts.Count > 0)
        {
            tip.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", parts),
                Margin = new Thickness(0, 3, 0, 0),
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return tip;
    }

    /// <summary>
    /// One band of a day's bar. Sub-cent slices are dropped rather than drawn as a hairline
    /// nobody can read, so a stack of three colours always means three real amounts.
    /// </summary>
    private void Segment(Panel column, decimal amount, decimal ceiling, string brush)
    {
        var height = ChartHeight * (double)(amount / ceiling);
        if (height < 1)
        {
            return;
        }

        column.Children.Add(new Border
        {
            Height = height,
            Background = (Brush)FindResource(brush),
        });
    }

    private UIElement BuildKey()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0),
        };

        Add("Accent", "tutor");
        Add("Success", "review");
        Add("TextFaint", "other");
        return row;

        void Add(string brush, string text)
        {
            row.Children.Add(new Border
            {
                Width = 9,
                Height = 9,
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                Background = (Brush)FindResource(brush),
            });

            row.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 16, 0),
                Foreground = (Brush)FindResource("TextFaint"),
            });
        }
    }

    /// <summary>The three figures that answer "am I going to run out", side by side.</summary>
    private UIElement BuildStats(UsageOutlook outlook)
    {
        var row = new Grid { Margin = new Thickness(0, 26, 0, 0), MaxWidth = 640 };
        for (var i = 0; i < 4; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition());
        }

        Add(0,
            "SPENT",
            $"${outlook.SpentThisMonth:0.00}",
            $"{outlook.MonthUsed * 100:0}% of the ${outlook.MonthlyCap:0.00} cap");

        // The cap gets a figure of its own rather than a clause inside today's. It is the number
        // that MOVES — the whole subject of the chart above — and buried in a sentence about
        // something else it read as "$0.29 of $0.27 already spent", which is not what it says.
        Add(1, "CAP", $"${outlook.AllowanceToday:0.00}", CapNote(outlook));

        Add(2,
            "TODAY",
            $"${outlook.SpentToday:0.00}",
            $"{outlook.TodayUsed * 100:0}% of today's cap");

        Add(3,
            "LEFT",
            $"${outlook.RemainingThisMonth:0.00}",
            outlook.QuestionsPerDayLeft is { } questions
                ? $"about {questions} question(s) a day"
                : $"for {outlook.DaysLeft} more day(s)");
        return row;

        void Add(int column, string label, string figure, string note)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
            stack.Children.Add(Label(label));
            stack.Children.Add(new TextBlock
            {
                Text = figure,
                FontSize = 23,
                Margin = new Thickness(0, 6, 0, 0),
                Foreground = (Brush)FindResource("TextTitle"),
            });
            stack.Children.Add(new TextBlock
            {
                Text = note,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Margin = new Thickness(0, 3, 0, 0),
                Foreground = (Brush)FindResource("TextFaint"),
            });

            Grid.SetColumn(stack, column);
            row.Children.Add(stack);
        }
    }

    /// <summary>
    /// How far today's cap has moved from the flat share, in percent and in questions.
    /// </summary>
    /// <remarks>
    /// Both, because they answer different halves. The percentage says the redistribution is
    /// working; the question count says what that is worth today, which is the only form of it
    /// anyone can act on.
    /// </remarks>
    private static string CapNote(UsageOutlook outlook)
    {
        var note = $"{outlook.ShareAgainstEven * 100:0}% of the original ${outlook.EvenShare:0.00}";

        return outlook.ExtraQuestionsPerDay is { } extra && extra != 0
            ? $"{note}, {(extra > 0 ? "+" : "−")}{Math.Abs(extra)} question(s) a day"
            : note;
    }

    private UIElement BuildDistribution(UsageOutlook outlook)
    {
        var block = new StackPanel { Margin = new Thickness(0, 30, 0, 0) };
        block.Children.Add(Label("WHERE THIS RATE LANDS"));

        var projection = outlook.ProjectedFraction;
        block.Children.Add(Body(
            $"${outlook.SpentPerDay:0.00} a day so far projects to ${outlook.ProjectedMonth:0.00} by "
            + $"month end, {projection * 100:0}% of the cap.",
            top: 12));

        block.Children.Add(Body(
            projection > 1.0
                ? "At this rate the cap arrives before the month does. Holding to "
                  + $"${outlook.RemainingThisMonth / Math.Max(1, outlook.DaysLeft):0.00} a day from here "
                  + "carries it to the end."
                : projection < 0.6
                    ? "Comfortably under, and every day you skip raises what the days after it may "
                      + "spend — there is room to lean on the tutor harder than you have been."
                    : "On track to finish the month inside the cap.",
            top: 8,
            emphasis: projection > 1.0));

        return block;
    }

    private TextBlock Label(string text) => new()
    {
        Text = text,
        Style = Application.Current.TryFindResource("MicroLabel") as Style,
    };

    private TextBlock Caption(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Margin = new Thickness(0, 6, 0, 0),
        Foreground = (Brush)FindResource("TextFaint"),
    };

    private TextBlock Body(string text, double top, bool emphasis = false) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, top, 0, 0),
        MaxWidth = 560,
        HorizontalAlignment = HorizontalAlignment.Left,
        Foreground = (Brush)FindResource(emphasis ? "Major" : "TextBody"),
    };
}
