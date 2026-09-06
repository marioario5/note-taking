using NoteTaker.Core.Models;
using NoteTaker.Core.Tutor;

namespace NoteTaker.Core.Abstractions;

public interface INotebookRepository
{
    Task<IReadOnlyList<Notebook>> GetNotebooksAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Section>> GetSectionsAsync(long notebookId, CancellationToken ct = default);
    Task<Notebook> CreateNotebookAsync(string name, CancellationToken ct = default);
    Task<Section> CreateSectionAsync(long notebookId, string name, CancellationToken ct = default);
    Task RenameNotebookAsync(long id, string name, CancellationToken ct = default);
    Task RenameSectionAsync(long id, string name, CancellationToken ct = default);
    Task DeleteNotebookAsync(long id, CancellationToken ct = default);
    Task DeleteSectionAsync(long id, CancellationToken ct = default);
}

public interface IPageRepository
{
    Task<IReadOnlyList<Page>> GetPagesAsync(long sectionId, CancellationToken ct = default);
    Task<Page?> GetPageAsync(long pageId, CancellationToken ct = default);
    Task<Page> CreatePageAsync(long sectionId, string title, CancellationToken ct = default);
    Task UpdatePageAsync(Page page, CancellationToken ct = default);
    Task DeletePageAsync(long pageId, CancellationToken ct = default);
    Task<IReadOnlyList<Page>> GetAllPagesAsync(CancellationToken ct = default);
}

public interface IInkRepository
{
    Task<InkData?> LoadAsync(long pageId, CancellationToken ct = default);

    /// <summary>Persists strokes and returns the new revision number.</summary>
    Task<int> SaveAsync(long pageId, byte[] isfBlob, CancellationToken ct = default);

    Task<long> SaveSnapshotAsync(long pageId, byte[] png, int strokeRevision, CancellationToken ct = default);
    Task<PageSnapshot?> GetSnapshotAsync(long snapshotId, CancellationToken ct = default);
    Task<PageSnapshot?> GetLatestSnapshotAsync(long pageId, CancellationToken ct = default);

    /// <summary>Keeps snapshot storage bounded by retaining only the newest few per page.</summary>
    Task PruneSnapshotsAsync(long pageId, int keep, CancellationToken ct = default);

    /// <summary>
    /// Pictures placed on the page (pasted, or produced by the graph/Python tools). Kept
    /// apart from the PDF background, which is re-derived on every open — and deliberately
    /// withheld from the mistake-flagging scan, though the chat tutor does see them.
    /// </summary>
    Task<IReadOnlyList<PageImage>> GetImagesAsync(long pageId, CancellationToken ct = default);

    Task<long> AddImageAsync(PageImage image, CancellationToken ct = default);

    /// <summary>Persists a new rectangle after the student moves or resizes a picture.</summary>
    Task UpdateImageBoundsAsync(PageImage image, CancellationToken ct = default);

    Task DeleteImageAsync(long imageId, CancellationToken ct = default);
}

public interface ITutorRepository
{
    Task<IReadOnlyList<TutorFeedback>> GetFeedbackAsync(long pageId, CancellationToken ct = default);

    /// <summary>
    /// Every finding ever recorded for this page, active and dismissed alike — including
    /// mistakes the student made and then fixed in place, which <see cref="DismissFeedbackAsync"/>
    /// soft-flags rather than deletes. This is the substrate Review mode mines for recurring
    /// weak topics; <see cref="GetFeedbackAsync"/> alone would miss anything already corrected.
    /// </summary>
    Task<IReadOnlyList<TutorFeedback>> GetAllFeedbackForPageAsync(long pageId, CancellationToken ct = default);

    Task<long> AddFeedbackAsync(TutorFeedback feedback, CancellationToken ct = default);

    /// <summary>Records one judgement about one skill, for Review to accumulate.</summary>
    Task<long> AddSkillEventAsync(SkillEvent skillEvent, CancellationToken ct = default);

    /// <summary>
    /// A topic's skill history since <paramref name="since"/>, newest first. Bounded by time
    /// because confidence decays — an old event is history, not evidence of current skill.
    /// </summary>
    Task<IReadOnlyList<SkillEvent>> GetSkillEventsAsync(
        long sectionId,
        DateTimeOffset since,
        CancellationToken ct = default);

    /// <summary>The topic's standing study report, or null if none has been written.</summary>
    Task<SkillReportRecord?> GetSkillReportAsync(long sectionId, CancellationToken ct = default);

    /// <summary>Replaces the topic's report. One per topic: this is a picture, not a diary.</summary>
    Task SaveSkillReportAsync(SkillReportRecord report, CancellationToken ct = default);
    Task ClearFeedbackAsync(long pageId, TutorMode? originMode = null, CancellationToken ct = default);
    Task DismissFeedbackAsync(long feedbackId, CancellationToken ct = default);

    /// <summary>
    /// Soft-dismisses every currently-active finding for a page without deleting any row —
    /// used by Review mode instead of <see cref="ClearFeedbackAsync"/> so a fresh scan can
    /// replace what's shown without erasing the history future scans reason about.
    /// </summary>
    Task DismissActiveFeedbackAsync(long pageId, CancellationToken ct = default);

    /// <summary>
    /// Finds the matching conversation, or starts one.
    /// </summary>
    /// <param name="notBefore">
    /// Only reuse a thread created at or after this moment. Callers pass the time the app
    /// started, so each run begins with a clean sidebar instead of resuming a conversation
    /// about work from days ago — and, just as importantly, the model is not handed stale
    /// turns describing a page that has since changed. Older threads stay in the database.
    /// </param>
    Task<TutorThread> GetOrCreateThreadAsync(
        long pageId,
        long? feedbackId,
        string title,
        ThreadKind kind = ThreadKind.General,
        DateTimeOffset? notBefore = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<TutorMessage>> GetMessagesAsync(long threadId, CancellationToken ct = default);
    Task AddMessageAsync(TutorMessage message, CancellationToken ct = default);

    Task<long> EnqueueJobAsync(TutorJob job, CancellationToken ct = default);
    Task<IReadOnlyList<TutorJob>> GetPendingJobsAsync(int limit, CancellationToken ct = default);
    Task UpdateJobAsync(TutorJob job, CancellationToken ct = default);

    /// <summary>Feedback raised since <paramref name="since"/>, used for the session pattern report.</summary>
    Task<IReadOnlyList<TutorFeedback>> GetFeedbackSinceAsync(DateTimeOffset since, CancellationToken ct = default);
}

public interface IEmbeddingRepository
{
    Task UpsertAsync(PageEmbedding embedding, CancellationToken ct = default);
    Task<PageEmbedding?> GetAsync(long pageId, CancellationToken ct = default);
    Task<IReadOnlyList<PageEmbedding>> GetAllAsync(CancellationToken ct = default);
    Task ReplaceRelatedAsync(long sourcePageId, IReadOnlyList<RelatedPage> related, CancellationToken ct = default);
    Task<IReadOnlyList<RelatedPage>> GetRelatedAsync(long pageId, CancellationToken ct = default);
}

public interface IUsageRepository
{
    Task LogAsync(ApiUsageLog entry, CancellationToken ct = default);
    Task<UsageSummary> GetSummaryAsync(DateTimeOffset since, CancellationToken ct = default);

    /// <summary>Spend grouped by call type and model, highest cost first.</summary>
    Task<IReadOnlyList<UsageBreakdownRow>> GetBreakdownAsync(
        DateTimeOffset since,
        CancellationToken ct = default);

    /// <summary>
    /// Cost per calendar day since <paramref name="since"/>, keyed by the day's start.
    /// </summary>
    /// <remarks>
    /// Grouped in SQL rather than by asking for one summary per day: the week strip and the
    /// month's distribution both want every day at once, and eight round trips to answer one
    /// screen is work the database can do in a single pass.
    /// </remarks>
    Task<IReadOnlyDictionary<DateTimeOffset, SpendSplit>> GetDailyCostsAsync(
        DateTimeOffset since,
        CancellationToken ct = default);
}
