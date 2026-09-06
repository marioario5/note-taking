using System.Windows.Media;
using NoteTaker.Core.Models;
using NoteTaker.Core.Tutor;

namespace NoteTaker.App.ViewModels;

/// <summary>
/// One finding as the sidebar shows it: a numbered badge that matches the chip drawn on the
/// page, a tracked severity label, and a rough position so you can find it without hunting.
/// </summary>
public sealed class FeedbackItemViewModel(TutorFeedback feedback, int number)
{
    private static readonly Brush MajorBrush =
        new SolidColorBrush(Color.FromRgb(0xE8, 0x8F, 0x74));

    private static readonly Brush MinorBrush =
        new SolidColorBrush(Color.FromRgb(0xFE, 0xBB, 0x55));

    private static readonly Brush NotationBrush =
        new SolidColorBrush(Color.FromRgb(0xE8, 0x9B, 0x2E));

    public TutorFeedback Feedback { get; } = feedback;

    public int Number { get; } = number;

    public string Label { get; } = feedback.Label;

    public string SeverityLabel { get; } = feedback.Severity switch
    {
        FeedbackSeverity.Major => "MAJOR",
        FeedbackSeverity.Minor => "MINOR",
        _ => "NOTATION",
    };

    public Brush SeverityBrush { get; } = feedback.Severity switch
    {
        FeedbackSeverity.Major => MajorBrush,
        FeedbackSeverity.Minor => MinorBrush,
        _ => NotationBrush,
    };

    /// <summary>
    /// The tutor works in normalized regions rather than lines, so this describes where on the
    /// page a finding sits instead of inventing a line number it cannot know.
    /// </summary>
    /// <remarks>
    /// The band vocabulary lives in <see cref="InkLineSnapper"/> because three places now have
    /// to agree on it: the scan prompts ask the model to answer in exactly these words, the
    /// snapper parses that answer back to pick a line of ink, and this renders it. A private
    /// copy here would be one edit away from silently disagreeing with the prompt.
    /// </remarks>
    public string Location { get; } = InkLineSnapper.DescribeBand(feedback.Region);
}
