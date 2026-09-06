namespace NoteTaker.Core.Models;

/// <summary>
/// Controls how the tutor observes a page. Practice performs no network calls at
/// all, so an exam-style session is never interrupted by a hint.
/// </summary>
public enum TutorMode
{
    /// <summary>Debounced checks while writing, with inline highlights.</summary>
    Live = 0,

    /// <summary>Total silence: strokes and snapshots stay on device.</summary>
    Practice = 1,

    /// <summary>Batch review after the fact, with Socratic follow-up.</summary>
    Review = 2,
}

public enum FeedbackSeverity
{
    Info = 0,
    Minor = 1,
    Major = 2,
}

public enum MessageRole
{
    System = 0,
    User = 1,
    Assistant = 2,
}

/// <summary>
/// Distinguishes the general "ask anything about this page" thread from a Review-mode
/// thread, since both are unanchored (FeedbackId IS NULL) and would otherwise collide.
/// </summary>
public enum ThreadKind
{
    General = 0,
    Review = 1,
}

/// <summary>Distinguishes billing categories in <see cref="ApiUsageLog"/>.</summary>
public enum TutorCallType
{
    LiveCheck = 0,
    ReviewScan = 1,
    SocraticChat = 2,
    PatternSummary = 3,
    PracticeGeneration = 4,

    /// <summary>Text-only call that turns a page's recurring mistake topics into a few fresh Socratic questions.</summary>
    WeaknessReview = 5,

    /// <summary>
    /// The written study report for one topic. Text-only and aggregate-only: it is sent counts,
    /// never the conversation those counts came from, which is what keeps it affordable enough
    /// to generate unattended.
    /// </summary>
    SkillReport = 6,

    /// <summary>
    /// Mark one attempt and say nothing. The student wants the work counted toward Review, not
    /// taught — so this sends the page and the shortest prompt in the app, and the whole reply
    /// is a verdict tag. No thread, no history, no tutoring.
    /// </summary>
    SkillCheck = 7,
}

public enum TutorJobState
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
}
