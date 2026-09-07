using NoteTaker.Core.Abstractions;
using NoteTaker.Core.Models;

namespace NoteTaker.Core.Tutor;

public sealed record TutorFeedbackBatch(
    long PageId,
    IReadOnlyList<TutorFeedback> Feedback,
    string Summary,
    TutorMode OriginMode);

public sealed record TutorStatus(string Message, bool IsBusy = false, bool IsBlocked = false);

/// <summary>
/// Result of a Review pass: the freshly (re-)scanned findings, plus — only when a
/// recurring weak topic was found in this page's history — the chat thread and opening
/// message a caller should switch the UI to.
///
/// <paramref name="Message"/> always describes what actually happened, even when nothing
/// else visible did (no findings drawn, no thread opened) — a clean page, an offline
/// queue, and a budget cap all look identical without it, and a caller that only shows
/// something when <see cref="Thread"/> is set would otherwise leave every one of those
/// outcomes looking like the click silently did nothing.
/// </summary>
public sealed record ReviewResult(
    IReadOnlyList<TutorFeedback> Findings,
    string Message,
    TutorThread? Thread = null,
    TutorMessage? Opening = null);

/// <summary>
/// Owns every decision about when the tutor is allowed to look at a page.
/// Practice mode is enforced here rather than in the UI, so no accidental call path
/// can leak a hint during an exam-style session.
/// </summary>
public sealed class TutorCoordinator : IDisposable
{
    private readonly ITutorClient _client;
    private readonly IPageSnapshotProvider _snapshots;
    private readonly IInkRepository _ink;
    private readonly ITutorRepository _tutor;
    private readonly IUsageRepository _usage;
    private readonly IConnectivity _connectivity;
    private readonly IClock _clock;
    private readonly TutorOptions _options;
    private readonly TutorBudget _budget;
    private long _lastImageThreadId;
    private byte[]? _lastImageHash;

    /// <summary>The most recent Shift+R verdict, and the page it judged.</summary>
    private (long PageId, SkillVerdict Verdict)? _lastMark;

    private readonly object _gate = new();
    private CancellationTokenSource? _debounceCts;
    private long _pendingPageId;
    private NormalizedRegion _pendingRegion;
    private NormalizedRegion _pendingLatest;
    private DateTimeOffset _lastLiveCall = DateTimeOffset.MinValue;

    public TutorCoordinator(
        ITutorClient client,
        IPageSnapshotProvider snapshots,
        IInkRepository ink,
        ITutorRepository tutor,
        IUsageRepository usage,
        IConnectivity connectivity,
        IClock clock,
        TutorOptions options)
    {
        _client = client;
        _snapshots = snapshots;
        _ink = ink;
        _tutor = tutor;
        _usage = usage;
        _connectivity = connectivity;
        _clock = clock;
        _options = options;
        _budget = new TutorBudget(usage, clock, options);
    }

    public event EventHandler<TutorFeedbackBatch>? FeedbackProduced;
    public event EventHandler<TutorStatus>? StatusChanged;

    public TutorOptions Options => _options;

    /// <summary>
    /// The tutor's judgement of the most recent chat turn, or null if it emitted no usable tag.
    /// </summary>
    /// <remarks>
    /// Exposed so the caller can reset the hint ladder when the work moves on. The ladder counts
    /// messages, which is only a proxy for struggle while the student is still on one mistake —
    /// once they are right, or have moved to a different skill, the count belongs to work that is
    /// finished and must not carry over.
    /// </remarks>
    public SkillVerdict? LastVerdict { get; private set; }

    /// <summary>
    /// Whether this picture is the one already sent on this thread.
    /// </summary>
    /// <remarks>
    /// Hashed rather than kept, so a long conversation holds 32 bytes instead of a PNG. Keyed to
    /// the thread: a different thread is a different conversation and has seen nothing, and
    /// switching page or anchor opens one.
    /// </remarks>
    private bool IsRepeatOfLastImage(long threadId, byte[] png)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(png);

        if (_lastImageThreadId == threadId && _lastImageHash is { } previous
            && previous.AsSpan().SequenceEqual(hash))
        {
            return true;
        }

        _lastImageThreadId = threadId;
        _lastImageHash = hash;
        return false;
    }

    /// <summary>Where today's spending stands, for the usage panel and the borrow dialog.</summary>
    public Task<BudgetSnapshot> BudgetSnapshotAsync(CancellationToken ct = default)
        => _budget.SnapshotAsync(_clock.UtcNow, ct);

    /// <summary>
    /// Raises today's ceiling, after the student has confirmed a dialog saying what it costs the
    /// rest of the month. Lasts until midnight Pacific and is not persisted.
    /// </summary>
    public void GrantBudgetBorrow(decimal amount) => _budget.GrantBorrow(amount);

    /// <summary>
    /// Called by the ink surface after every stroke. Only Live mode schedules work;
    /// Practice and Review return immediately without touching the network.
    /// </summary>
    public void NotifyInkChanged(long pageId, TutorMode mode, NormalizedRegion changedRegion)
    {
        if (mode != TutorMode.Live)
        {
            return;
        }

        // The prune that used to run right here now happens in DebounceThenScanAsync, once
        // the pen has actually paused. It is not cheap — one dispatcher round-trip per open
        // finding, each hit-testing every point of every stroke on the page — and firing it
        // from here meant every completed stroke queued that burst against the UI thread
        // just as the next stroke was starting. That is why a quick second mark (the
        // superscript in "x squared") was the one that went missing.
        CancellationToken token;
        lock (_gate)
        {
            _pendingPageId = pageId;
            _pendingLatest = changedRegion;
            _pendingRegion = _pendingRegion.IsEmpty
                ? changedRegion
                : CapFocusRegion(_pendingRegion.Union(changedRegion));

            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            token = _debounceCts.Token;
        }

        _ = DebounceThenScanAsync(token);
    }

    /// <summary>
    /// Dismisses findings whose regions no longer contain ink (strict hit-test).
    /// Call on page load so blank red marks from a prior session do not linger.
    /// </summary>
    public Task SyncFeedbackWithInkAsync(long pageId, CancellationToken ct = default) =>
        PruneEmptyFeedbackAsync(pageId, ct);

    /// <summary>
    /// Dismisses findings whose regions no longer contain ink. No API call.
    /// </summary>
    private async Task PruneEmptyFeedbackAsync(long pageId, CancellationToken ct = default)
    {
        try
        {
            var items = await _tutor.GetFeedbackAsync(pageId, ct).ConfigureAwait(false);
            if (items.Count == 0)
            {
                return;
            }

            var kept = new List<TutorFeedback>(items.Count);
            var removed = false;

            foreach (var item in items)
            {
                if (await _snapshots
                        .RegionContainsInkAsync(pageId, item.Region, InkHitTolerance.Strict, ct)
                        .ConfigureAwait(false))
                {
                    kept.Add(item);
                    continue;
                }

                await _tutor.DismissFeedbackAsync(item.Id, ct).ConfigureAwait(false);
                removed = true;
            }

            if (!removed)
            {
                return;
            }

            FeedbackProduced?.Invoke(
                this,
                new TutorFeedbackBatch(pageId, kept, Summary: string.Empty, TutorMode.Live));

            RaiseStatus(new TutorStatus(
                kept.Count == 0 ? "Cleared — that ink is gone." : $"{kept.Count} still to look at."));
        }
        catch (Exception ex)
        {
            RaiseStatus(new TutorStatus($"Could not refresh marks: {ex.Message}", IsBlocked: true));
        }
    }

    /// <summary>Cancels any scheduled live check, e.g. when switching into Practice mode.</summary>
    public void CancelPending()
    {
        lock (_gate)
        {
            _debounceCts?.Cancel();
            _pendingRegion = default;
            _pendingLatest = default;
        }
    }

    private async Task DebounceThenScanAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_options.LiveDebounce, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        long pageId;
        NormalizedRegion region;
        lock (_gate)
        {
            pageId = _pendingPageId;
            // Prefer where the student just wrote so later mistakes are not missed
            // because an earlier union was recentered on old work.
            region = CapFocusRegion(
                _pendingLatest.IsEmpty ? _pendingRegion : _pendingLatest.Inflate(0.08));
            _pendingRegion = default;
            _pendingLatest = default;
        }

        // Now that the pen has paused, clear any marks whose ink was erased. Same work as
        // before, just no longer competing with an in-progress stroke — and it still runs
        // well before the scan below, so a stale box cannot outlive the debounce.
        await PruneEmptyFeedbackAsync(pageId, token).ConfigureAwait(false);

        try
        {
            await RunLiveCheckAsync(pageId, region, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer stroke; nothing to report.
        }
        catch (Exception ex)
        {
            RaiseStatus(new TutorStatus($"Tutor check failed: {ex.Message}", IsBlocked: true));
        }
    }

    /// <summary>
    /// How far outside the new writing an existing mark still counts as part of what the
    /// student was just working on. Roughly a line of handwriting.
    /// </summary>
    private const double NeighbouringMarkReach = 0.05;

    /// <summary>
    /// Widens the scan window to take in marks sitting right beside the new ink, so the model
    /// is actually asked whether they still stand.
    /// </summary>
    /// <remarks>
    /// A mark is only cleared when the scan that covered it comes back silent about it —
    /// deliberately, so an untouched mistake elsewhere on the page is never mistaken for one
    /// that has been re-examined and found clean. The gap is a mistake fixed from OUTSIDE its
    /// own box: draw a lone integral sign, get it flagged, then write the rest of the
    /// expression beside it. The sign is now fine, but the ink inside its box never changed
    /// and the new writing lands next to it rather than on it, so the scan window excluded it
    /// and nothing could retire the mark.
    ///
    /// Pulling the neighbour into the window fixes it at the root: the model evaluates the
    /// completed expression, says nothing about the sign, and the existing rule clears it.
    /// The alternative — treating silence about regions the model was never shown as proof of
    /// correctness — would clear unrelated marks across the page.
    /// </remarks>
    private async Task<NormalizedRegion> IncludeNeighbouringMarksAsync(
        long pageId,
        NormalizedRegion region,
        CancellationToken ct)
    {
        if (region.IsEmpty)
        {
            return region;
        }

        var neighbourhood = region.Inflate(NeighbouringMarkReach);
        var existing = await _tutor.GetFeedbackAsync(pageId, ct).ConfigureAwait(false);

        var widened = region;
        foreach (var mark in existing)
        {
            if (mark.OriginMode == TutorMode.Live
                && !mark.Region.IsEmpty
                && mark.Region.Intersects(neighbourhood))
            {
                widened = widened.Union(mark.Region);
            }
        }

        return widened;
    }

    private async Task RunLiveCheckAsync(long pageId, NormalizedRegion region, CancellationToken ct)
    {
        if (!_options.VisionEnabled)
        {
            return;
        }

        var now = _clock.UtcNow;
        if (now - _lastLiveCall < _options.MinLiveInterval)
        {
            return;
        }

        var decision = await _budget.CheckAsync(TutorCallType.LiveCheck, ct).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            RaiseStatus(new TutorStatus(decision.Reason!, IsBlocked: true));
            return;
        }

        region = await IncludeNeighbouringMarksAsync(pageId, region, ct).ConfigureAwait(false);

        // Pass the focus through to the capture itself: a live check knows roughly where the
        // new writing is, so there is no reason to rasterize (and spend tokens on) a window
        // sized for "no idea where to look".
        var capture = await _snapshots
            .CaptureAsync(pageId, region.IsEmpty ? null : region, ct)
            .ConfigureAwait(false);
        if (capture is null || capture.Png.Length == 0)
        {
            return;
        }

        var snapshotId = await _ink.SaveSnapshotAsync(pageId, capture.Png, capture.StrokeRevision, ct)
            .ConfigureAwait(false);
        await _ink.PruneSnapshotsAsync(pageId, _options.SnapshotRetentionPerPage, ct).ConfigureAwait(false);

        if (!_connectivity.IsOnline)
        {
            await _tutor.EnqueueJobAsync(
                new TutorJob
                {
                    PageId = pageId,
                    SnapshotId = snapshotId,
                    CallType = TutorCallType.LiveCheck,
                    OriginMode = TutorMode.Live,
                    CreatedAt = now,
                }, ct).ConfigureAwait(false);

            RaiseStatus(new TutorStatus("Offline — tutor check queued."));
            return;
        }

        RaiseStatus(new TutorStatus("Checking your work…", IsBusy: true));

        var result = await _client.ScanPageAsync(
            new TutorScanRequest(pageId, capture.Png, TutorMode.Live, region.IsEmpty ? null : region),
            ct).ConfigureAwait(false);

        // Recorded here — after the call actually completed — not before it, which is what
        // this was until the fix. The debounce token is cancelled and replaced on every new
        // stroke (NotifyInkChanged), so a scan aborted mid-flight threw OperationCanceledException
        // out of ScanPageAsync above and never reached this line — but the OLD placement, before
        // the call, had already burned the 20s MinLiveInterval and an hourly budget slot for a
        // check that never happened. A student who kept writing could starve every scan for the
        // rest of the session: each attempt got cancelled by the next stroke, yet each one still
        // counted as "just checked". Recording only on genuine completion means an aborted
        // attempt costs nothing and the very next pause gets an honest try.
        _lastLiveCall = now;
        _budget.RecordLiveCall();

        await PersistScanAsync(
                pageId,
                snapshotId,
                TutorMode.Live,
                TutorCallType.LiveCheck,
                result,
                capture.Area,
                ct,
                focus: region.IsEmpty ? null : region)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Review mode entry point. Runs the same thorough vision scan as before; whenever it
    /// finds at least one mistake, one cheap text-only call turns everything just found
    /// into a holistic opening Socratic message ending in one targeting question, and a
    /// chat thread is opened for it — no images resent, no vision call spent beyond the
    /// scan above. This page's full mistake history (active AND dismissed findings —
    /// corrected slips included) is also mined for topics that recur, purely to enrich
    /// that message ("you've hit this same kind of mistake before") — it no longer gates
    /// whether the chat opens at all. A genuinely clean page still gets no chat, nothing
    /// to review.
    /// </summary>
    public async Task<ReviewResult> ReviewPageAsync(long pageId, CancellationToken ct = default)
    {
        if (!_options.VisionEnabled)
        {
            // Returned for the caller's dialog, deliberately NOT raised as a status: the
            // status line is persistent and nothing else will ever overwrite it while page
            // checking is off, so raising here would pin a permanent notice to the window
            // over a setting the student chose on purpose.
            return new ReviewResult([], "Page checking is off. Ask about the page instead.");
        }

        var decision = await _budget.CheckAsync(TutorCallType.ReviewScan, ct).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            RaiseStatus(new TutorStatus(decision.Reason!, IsBlocked: true));
            return new ReviewResult([], decision.Reason!);
        }

        // No focus: Review is a thorough full-page pass, not a narrow follow-up on one edit.
        var capture = await _snapshots.CaptureAsync(pageId, ct: ct).ConfigureAwait(false);
        if (capture is null || capture.Png.Length == 0)
        {
            const string message = "Couldn't capture the page just now — try again.";
            RaiseStatus(new TutorStatus(message));
            return new ReviewResult([], message);
        }

        var snapshotId = await _ink.SaveSnapshotAsync(pageId, capture.Png, capture.StrokeRevision, ct)
            .ConfigureAwait(false);

        if (!_connectivity.IsOnline)
        {
            await _tutor.EnqueueJobAsync(
                new TutorJob
                {
                    PageId = pageId,
                    SnapshotId = snapshotId,
                    CallType = TutorCallType.ReviewScan,
                    OriginMode = TutorMode.Review,
                    CreatedAt = _clock.UtcNow,
                }, ct).ConfigureAwait(false);

            const string message = "Offline — review queued until you reconnect.";
            RaiseStatus(new TutorStatus(message));
            return new ReviewResult([], message);
        }

        RaiseStatus(new TutorStatus("Reviewing page…", IsBusy: true));

        var result = await _client.ScanPageAsync(
            new TutorScanRequest(pageId, capture.Png, TutorMode.Review),
            ct).ConfigureAwait(false);

        var fresh = await PersistScanAsync(
                pageId, snapshotId, TutorMode.Review, TutorCallType.ReviewScan, result, capture.Area, ct)
            .ConfigureAwait(false);

        if (fresh.Count == 0)
        {
            const string message = "No issues spotted.";
            RaiseStatus(new TutorStatus(message));
            return new ReviewResult(fresh, message);
        }

        var reviewDecision = await _budget.CheckAsync(TutorCallType.WeaknessReview, ct).ConfigureAwait(false);
        if (!reviewDecision.Allowed)
        {
            var message = $"{fresh.Count} to look at.";
            RaiseStatus(new TutorStatus(message));
            return new ReviewResult(fresh, message);
        }

        RaiseStatus(new TutorStatus("Reviewing your work…", IsBusy: true));

        // Zero-token step: everything ever recorded for this page, including mistakes the
        // student already fixed (soft-dismissed, never deleted — see DismissActiveFeedbackAsync
        // below), grouped by topic entirely in memory. Purely additional context for the
        // holistic message below, not a gate on whether it gets sent.
        var history = await _tutor.GetAllFeedbackForPageAsync(pageId, ct).ConfigureAwait(false);
        var weakTopics = WeaknessAggregator.Rank(history);

        var reviewReply = await _client.GenerateWeaknessReviewAsync(fresh, weakTopics, ct).ConfigureAwait(false);
        await LogUsageAsync(TutorCallType.WeaknessReview, reviewReply.Model, reviewReply.Usage, ct)
            .ConfigureAwait(false);

        var thread = await _tutor
            .GetOrCreateThreadAsync(pageId, null, "Review", ThreadKind.Review, _options.SessionStart, ct)
            .ConfigureAwait(false);

        var opening = new TutorMessage
        {
            ThreadId = thread.Id,
            Role = MessageRole.Assistant,
            Content = reviewReply.Content,
            CreatedAt = _clock.UtcNow,
        };
        await _tutor.AddMessageAsync(opening, ct).ConfigureAwait(false);

        var doneMessage = $"{fresh.Count} to look at — opened a review.";
        RaiseStatus(new TutorStatus(doneMessage));
        return new ReviewResult(fresh, doneMessage, thread, opening);
    }

    /// <summary>
    /// Socratic follow-up. <paramref name="anchor"/> is whichever finding the caller has
    /// resolved this question to belong to right now — every call gets a fresh crop of
    /// exactly that finding (or, when <paramref name="anchor"/> is null, a readable crop of
    /// the whole page). There is no cached "setup" image from when the thread first opened:
    /// the anchor can change turn to turn, and a stale image would talk past whatever the
    /// student actually just moved on to.
    /// </summary>
    /// <param name="ladderTurn">
    /// Hint-ladder turn for this open chat session (1-based). When set, overrides
    /// full thread history length so reopening an old thread does not unlock reveal.
    /// </param>
    /// <param name="onDelta">
    /// Optional observer for the reply as it streams in, so the sidebar can render text within
    /// about a second rather than after the whole answer. Purely additive: the returned message
    /// is still the complete reply, and it is that — not the deltas — which gets persisted and
    /// costed. Raised off the UI thread.
    /// </param>
    /// <param name="sectionId">
    /// The topic this turn belongs to. Passed in rather than looked up: the coordinator has no
    /// page repository, and the caller already holds the open page. Zero means "unknown", which
    /// simply records no skill history rather than guessing at a topic.
    /// </param>
    public async Task<TutorMessage?> AskAsync(
        long pageId,
        long threadId,
        string question,
        TutorFeedback? anchor,
        int? ladderTurn = null,
        Action<string>? onDelta = null,
        long sectionId = 0,
        CancellationToken ct = default)
    {
        var decision = await _budget.CheckAsync(TutorCallType.SocraticChat, ct).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            RaiseStatus(new TutorStatus(decision.Reason!, IsBlocked: true));
            return null;
        }

        var history = await _tutor.GetMessagesAsync(threadId, ct).ConfigureAwait(false);

        await _tutor.AddMessageAsync(
            new TutorMessage
            {
                ThreadId = threadId,
                Role = MessageRole.User,
                Content = question,
                CreatedAt = _clock.UtcNow,
            }, ct).ConfigureAwait(false);

        // Fresh high-contrast crop every turn so the chat model is not stuck on a faint setup image.
        byte[]? crop = await _snapshots
            .CaptureChatContextAsync(pageId, anchor is null || anchor.Region.IsEmpty ? null : anchor.Region, ct)
            .ConfigureAwait(false);

        // Asking a follow-up about work already on screen re-sent the same picture every turn,
        // and an image is the most expensive thing in the request by a wide margin. The render
        // is deterministic, so identical bytes mean nothing the model can see has changed —
        // same ink, same stamps, same crop — and the picture it already has still stands.
        var unchanged = crop is not null && IsRepeatOfLastImage(threadId, crop);
        if (unchanged)
        {
            crop = null;
        }

        // A mark made on this page moments ago is context the next question should have: the
        // student's "is this right?" has already been answered once, and without this the tutor
        // pays to work the same page out again from scratch. Consumed on use, so it colours the
        // turn that follows the mark and no others.
        if (_lastMark is { } mark && mark.PageId == pageId)
        {
            _lastMark = null;
            question +=
                $"\n\n(A marking pass just judged this page: {mark.Verdict.Skill}, "
                + $"{mark.Verdict.Outcome.ToString().ToLowerInvariant()}"
                + (string.IsNullOrWhiteSpace(mark.Verdict.Reason)
                    ? ").)"
                    : $" — {mark.Verdict.Reason}.)");
        }

        var historyTurns = history.Count(m => m.Role == MessageRole.User) + 1;
        var userTurns = ladderTurn ?? historyTurns;

        RaiseStatus(new TutorStatus("Thinking…", IsBusy: true));

        var result = await _client.ContinueThreadAsync(
            new TutorChatRequest(
                pageId,
                threadId,
                history,
                question,
                crop,
                anchor?.Label,
                Socratic: true,
                UserTurnCount: userTurns,
                PageUnchanged: unchanged),
            onDelta,
            ct).ConfigureAwait(false);

        var reply = new TutorMessage
        {
            ThreadId = threadId,
            Role = MessageRole.Assistant,
            Content = result.Content,
            CreatedAt = _clock.UtcNow,
        };

        await _tutor.AddMessageAsync(reply, ct).ConfigureAwait(false);
        await LogUsageAsync(TutorCallType.SocraticChat, result.Model, result.Usage, ct).ConfigureAwait(false);

        // The tutor's own judgement of this turn, banked against the topic so Review can build
        // a picture across sessions. Absent whenever the model skipped or malformed the tag —
        // one lost data point, never a failed turn.
        LastVerdict = result.Verdict;

        if (result.Verdict is { } verdict && sectionId > 0)
        {
            await _tutor.AddSkillEventAsync(
                new SkillEvent
                {
                    SectionId = sectionId,
                    PageId = pageId,
                    Skill = verdict.Skill,
                    Outcome = verdict.Outcome,
                    Reason = verdict.Reason,
                    Source = "tutor",
                    CreatedAt = _clock.UtcNow,
                },
                ct).ConfigureAwait(false);
        }

        RaiseStatus(new TutorStatus(string.Empty));
        return reply;
    }

    /// <summary>
    /// Marks the work on the page and banks it against the topic, without a word of tutoring.
    /// </summary>
    /// <remarks>
    /// For practising without wanting help. Doing this through the chat cost a full tutoring
    /// turn — the prompt, the whole conversation resent, and room for a paragraph of reply — to
    /// obtain one data point Review could use, and left an unread lesson in the transcript.
    ///
    /// Returns what was recorded so the caller can say so, or null when the page gave nothing to
    /// judge. A page the model cannot read records nothing rather than a guess.
    /// </remarks>
    public async Task<SkillVerdict?> RecordAttemptAsync(
        long pageId,
        long sectionId,
        CancellationToken ct = default)
    {
        var decision = await _budget.CheckAsync(TutorCallType.SkillCheck, ct).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            RaiseStatus(new TutorStatus(decision.Reason!, IsBlocked: true));
            return null;
        }

        RaiseStatus(new TutorStatus("Marking…", IsBusy: true));

        var page = await _snapshots.CaptureChatContextAsync(pageId, null, ct).ConfigureAwait(false);
        var result = await _client.CheckSkillAsync(page, ct).ConfigureAwait(false);

        await LogUsageAsync(TutorCallType.SkillCheck, result.Model, result.Usage, ct).ConfigureAwait(false);

        if (result.Verdict is not { } verdict || sectionId <= 0)
        {
            RaiseStatus(new TutorStatus("Nothing to mark on this page."));
            return null;
        }

        await _tutor.AddSkillEventAsync(
            new SkillEvent
            {
                SectionId = sectionId,
                PageId = pageId,
                Skill = verdict.Skill,
                Outcome = verdict.Outcome,
                Reason = verdict.Reason,

                // Recorded as the tutor's own judgement, because it is: the same model reading
                // the same page against the same skill list. Review weights it identically.
                Source = "tutor",
                CreatedAt = _clock.UtcNow,
            },
            ct).ConfigureAwait(false);

        // Handed to the next chat turn on this page. The model cannot carry its reasoning from
        // one request to the next — thinking tokens are internal to the call that produced them
        // and there is no API to replay them — but the CONCLUSION of that reasoning is this tag,
        // and passing it forward saves the tutor re-deriving a diagnosis that was just paid for.
        _lastMark = (pageId, verdict);

        RaiseStatus(new TutorStatus($"Recorded: {verdict.Skill} — {verdict.Outcome.ToString().ToLowerInvariant()}"));
        return verdict;
    }

    /// <summary>
    /// Closes out a study session with one text call that looks for recurring mistakes.
    /// </summary>
    public async Task<string> SummarizeSessionAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        var feedback = await _tutor.GetFeedbackSinceAsync(since, ct).ConfigureAwait(false);
        if (feedback.Count == 0)
        {
            return "No mistakes were flagged in this session.";
        }

        var decision = await _budget.CheckAsync(TutorCallType.PatternSummary, ct).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            return decision.Reason!;
        }

        RaiseStatus(new TutorStatus("Looking for patterns…", IsBusy: true));

        var result = await _client.SummarizePatternsAsync(feedback, ct).ConfigureAwait(false);
        await LogUsageAsync(TutorCallType.PatternSummary, result.Model, result.Usage, ct).ConfigureAwait(false);

        RaiseStatus(new TutorStatus(string.Empty));
        return result.Content;
    }

    /// <summary>Replays checks captured while offline or over budget.</summary>
    public async Task<int> FlushPendingJobsAsync(int limit = 10, CancellationToken ct = default)
    {
        // Every pending job is a scan. Without this, turning vision off would still bill for
        // whatever the queue accumulated the last time the app ran online — the saving would
        // silently arrive one reconnect late.
        if (!_options.VisionEnabled || !_connectivity.IsOnline)
        {
            return 0;
        }

        var jobs = await _tutor.GetPendingJobsAsync(limit, ct).ConfigureAwait(false);
        var processed = 0;

        foreach (var job in jobs)
        {
            var decision = await _budget.CheckAsync(job.CallType, ct).ConfigureAwait(false);
            if (!decision.Allowed)
            {
                break;
            }

            var snapshot = await _ink.GetSnapshotAsync(job.SnapshotId, ct).ConfigureAwait(false);
            if (snapshot is null)
            {
                job.State = TutorJobState.Failed;
                job.LastError = "Snapshot no longer available.";
                await _tutor.UpdateJobAsync(job, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                var result = await _client.ScanPageAsync(
                    new TutorScanRequest(job.PageId, snapshot.PngBlob, job.OriginMode),
                    ct).ConfigureAwait(false);

                // A queued snapshot predates knowing which part of the page it showed, so
                // treat it as the whole sheet — the identity mapping, i.e. exactly how every
                // scan behaved before captures started following the student off-sheet.
                await PersistScanAsync(
                        job.PageId,
                        job.SnapshotId,
                        job.OriginMode,
                        job.CallType,
                        result,
                        PageSnapshotCapture.WholeSheet,
                        ct)
                    .ConfigureAwait(false);

                job.State = TutorJobState.Completed;
                processed++;
            }
            catch (Exception ex)
            {
                job.AttemptCount++;
                job.LastError = ex.Message;
                job.State = job.AttemptCount >= 3 ? TutorJobState.Failed : TutorJobState.Pending;
            }

            await _tutor.UpdateJobAsync(job, ct).ConfigureAwait(false);
        }

        return processed;
    }

    /// <summary>
    /// Rewrites a box the model gave in image coordinates into sheet coordinates.
    /// </summary>
    /// <remarks>
    /// A vision model answers relative to the picture it was handed, which is only the same
    /// thing as the page when the picture happens to be the whole sheet. Once the capture
    /// follows the student onto open canvas the two diverge, and skipping this step would
    /// scatter every highlight across the sheet regardless of where the mistake actually is.
    /// </remarks>
    private static NormalizedRegion FromCaptureSpace(NormalizedRegion inImage, NormalizedRegion area) =>
        new(
            area.X + (inImage.X * area.Width),
            area.Y + (inImage.Y * area.Height),
            inImage.Width * area.Width,
            inImage.Height * area.Height);

    private async Task<IReadOnlyList<TutorFeedback>> PersistScanAsync(
        long pageId,
        long snapshotId,
        TutorMode mode,
        TutorCallType callType,
        TutorScanResult result,
        NormalizedRegion captureArea,
        CancellationToken ct,
        NormalizedRegion? focus = null)
    {
        var mapped = result.Findings
            .Select(f => f with { Region = FromCaptureSpace(f.Region, captureArea) })
            .ToList();

        var findings = FindingDeduper.Dedupe(mapped);

        if (mode == TutorMode.Live)
        {
            return await PersistLiveFindingsAsync(pageId, snapshotId, findings, result, focus, ct)
                .ConfigureAwait(false);
        }

        // Full-page check replaces every mark on the page (Live leftovers included), so
        // status text and red boxes stay in sync — but soft-dismiss rather than delete:
        // Review mines this page's full history (including corrected mistakes) for
        // recurring weak topics, so nothing here can be a hard delete anymore.
        await _tutor.DismissActiveFeedbackAsync(pageId, ct).ConfigureAwait(false);

        var acceptedFindings = new List<TutorRegionFinding>();
        foreach (var finding in findings)
        {
            var accepted = await AcceptFindingRegionAsync(pageId, finding, ct).ConfigureAwait(false);
            if (accepted is not null)
            {
                acceptedFindings.Add(accepted);
            }
        }

        // AcceptFindingRegionAsync can nudge a box up to 0.04 to land on ink. Two boxes that
        // cleared the first Dedupe pass (on the model's raw, pre-acceptance boxes) can end up
        // overlapping the same ink once each is independently expanded — re-run Dedupe here so
        // that expansion doesn't reintroduce the duplicate mark the first pass already ruled out.
        var acceptedDeduped = FindingDeduper.Dedupe(acceptedFindings);

        var saved = new List<TutorFeedback>(acceptedDeduped.Count);
        foreach (var finding in acceptedDeduped)
        {
            saved.Add(await InsertFindingAsync(pageId, snapshotId, mode, result.Model, finding, ct)
                .ConfigureAwait(false));
        }

        await LogUsageAsync(callType, result.Model, result.Usage, ct).ConfigureAwait(false);

        FeedbackProduced?.Invoke(this, new TutorFeedbackBatch(pageId, saved, result.SidebarSummary, mode));
        RaiseStatus(new TutorStatus(saved.Count == 0 ? "No issues spotted." : $"{saved.Count} to look at."));

        return saved;
    }

    /// <summary>
    /// Live scans merge into existing marks: only accept findings near the recent ink focus,
    /// replace overlaps, and leave settled marks elsewhere alone so the same slip is not
    /// re-flagged every debounce.
    /// </summary>
    private async Task<IReadOnlyList<TutorFeedback>> PersistLiveFindingsAsync(
        long pageId,
        long snapshotId,
        IReadOnlyList<TutorRegionFinding> findings,
        TutorScanResult result,
        NormalizedRegion? focus,
        CancellationToken ct)
    {
        var existing = (await _tutor.GetFeedbackAsync(pageId, ct).ConfigureAwait(false))
            .Where(item => item.OriginMode == TutorMode.Live)
            .ToList();

        var focusGate = focus is { IsEmpty: false } region
            ? region.Inflate(0.14)
            : (NormalizedRegion?)null;

        var incoming = new List<TutorRegionFinding>();
        foreach (var finding in findings)
        {
            var accepted = await AcceptFindingRegionAsync(pageId, finding, ct).ConfigureAwait(false);
            if (accepted is null)
            {
                continue;
            }

            // With a focus window, ignore re-reports of settled work far from the new ink.
            if (focusGate is { } gate && !accepted.Region.Intersects(gate))
            {
                continue;
            }

            incoming.Add(accepted);
        }

        // Same reason as the Review path: acceptance can expand a box onto ink, which can
        // make two findings that were distinct pre-expansion overlap post-expansion.
        incoming = FindingDeduper.Dedupe(incoming).ToList();

        foreach (var old in existing)
        {
            var replaced = incoming.Any(item => FindingDeduper.Overlaps(item.Region, old.Region));
            var stillHasInk = await _snapshots
                .RegionContainsInkAsync(pageId, old.Region, InkHitTolerance.Strict, ct)
                .ConfigureAwait(false);

            // The model is only told about the RAW focus window in its instructions ("the
            // student just wrote near x=..., y=..."), never the extra 0.14 padding in
            // focusGate above — that padding exists purely to be generous about ACCEPTING an
            // incoming finding near the edit, not to define what the model promised to look
            // at. Using focusGate here over-reached that promise: an unrelated mistake on a
            // completely different equation could sit inside that padding, and this scan's
            // silence about it (because the model was never asked about it) would get
            // misread as "re-examined and now clean" instead of "out of scope." Checking
            // against the raw focus keeps this to exactly what the model actually
            // evaluated — still enough to clear a mistake fixed in place, since that
            // correction sits inside its own recent-writing focus by construction.
            var rescannedClean = focus is { IsEmpty: false } window && old.Region.Intersects(window) && !replaced;

            if (replaced || rescannedClean || !stillHasInk)
            {
                await _tutor.DismissFeedbackAsync(old.Id, ct).ConfigureAwait(false);
            }
        }

        var surviving = (await _tutor.GetFeedbackAsync(pageId, ct).ConfigureAwait(false))
            .Where(item => item.OriginMode == TutorMode.Live)
            .ToList();

        var insertedCount = 0;
        var saved = new List<TutorFeedback>(surviving);
        foreach (var finding in incoming)
        {
            if (surviving.Any(item => FindingDeduper.Overlaps(item.Region, finding.Region)))
            {
                continue;
            }

            var inserted = await InsertFindingAsync(
                    pageId, snapshotId, TutorMode.Live, result.Model, finding, ct)
                .ConfigureAwait(false);
            insertedCount++;
            saved.Add(inserted);
            // Keep this round's own inserts visible to the overlap check above, so two
            // incoming findings that only started overlapping after independent expansion
            // (and were not already caught by the Dedupe pass just above) can't both land.
            surviving.Add(inserted);
        }

        await LogUsageAsync(TutorCallType.LiveCheck, result.Model, result.Usage, ct).ConfigureAwait(false);

        FeedbackProduced?.Invoke(
            this,
            new TutorFeedbackBatch(pageId, saved, result.SidebarSummary, TutorMode.Live));

        // Do not claim "no issues" when older Live marks are still on the page.
        var status = saved.Count == 0
            ? "No issues spotted."
            : insertedCount > 0
                ? $"{saved.Count} to look at."
                : $"{saved.Count} still marked.";
        RaiseStatus(new TutorStatus(status));

        return saved;
    }

    /// <summary>
    /// Places a finding on the student's actual ink.
    /// </summary>
    /// <remarks>
    /// Snapping is tried FIRST, ahead of the older accept-or-expand path, because a model box
    /// covering some ink is not the same as a model box covering the RIGHT ink. Localization is
    /// these models' weakest output by a wide margin (arXiv 2501.07244: ~0.45 against ~0.65 for
    /// detection), so a box that happens to overlap a stroke could still be half a line off and
    /// the old check would have waved it through unchanged.
    ///
    /// The expand-onto-nearby-ink fallback stays for when snapping declines — it is the
    /// behaviour that has been shipping, and a finding placed slightly off is better than a
    /// finding silently dropped.
    /// </remarks>
    private async Task<TutorRegionFinding?> AcceptFindingRegionAsync(
        long pageId,
        TutorRegionFinding finding,
        CancellationToken ct)
    {
        var snapped = await _snapshots
            .SnapToInkAsync(pageId, finding.Region, finding.Where, ct)
            .ConfigureAwait(false);

        if (snapped is { IsEmpty: false } onInk)
        {
            return finding with { Region = onInk };
        }

        if (await _snapshots
                .RegionContainsInkAsync(pageId, finding.Region, InkHitTolerance.Loose, ct)
                .ConfigureAwait(false))
        {
            return finding;
        }

        var expanded = finding.Region.Inflate(0.04);
        if (await _snapshots
                .RegionContainsInkAsync(pageId, expanded, InkHitTolerance.Loose, ct)
                .ConfigureAwait(false))
        {
            return finding with { Region = expanded };
        }

        return null;
    }

    private async Task<TutorFeedback> InsertFindingAsync(
        long pageId,
        long snapshotId,
        TutorMode mode,
        string model,
        TutorRegionFinding finding,
        CancellationToken ct)
    {
        var feedback = new TutorFeedback
        {
            PageId = pageId,
            SnapshotId = snapshotId,
            Region = finding.Region,
            Severity = finding.Severity,
            Label = finding.Label,
            Topic = finding.Topic,
            Reading = finding.Reading,
            PositionHint = finding.Where,
            Model = model,
            OriginMode = mode,
            CreatedAt = _clock.UtcNow,
        };

        feedback.Id = await _tutor.AddFeedbackAsync(feedback, ct).ConfigureAwait(false);
        return feedback;
    }

    /// <summary>
    /// Returns the topic's study report, writing a fresh one only when it has earned it.
    /// </summary>
    /// <remarks>
    /// The report is the one part of Review that spends money, and it is reached by opening a
    /// page rather than by pressing a button, so every guard sits here: the gate decides whether
    /// there is anything new to say, and the budget decides whether there is anything left to
    /// say it with. A refusal returns the report already on file rather than nothing, so opening
    /// Review over budget still shows the last thing written.
    /// </remarks>
    public async Task<SkillReportRecord?> EnsureTopicReportAsync(
        long sectionId,
        string topicName,
        TopicConfidence topic,
        CancellationToken ct = default)
    {
        var existing = await _tutor.GetSkillReportAsync(sectionId, ct).ConfigureAwait(false);

        if (!SkillReportGate.Decide(topic, existing).Generate)
        {
            return existing;
        }

        return await WriteTopicReportAsync(sectionId, topicName, topic, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a fresh report for the topic, because it was asked for.
    /// </summary>
    /// <remarks>
    /// No gate. <see cref="SkillReportGate"/> exists to stop an automatic report firing on
    /// numbers that have not moved; a student pressing the button has already decided the
    /// report is worth writing, and second-guessing that would leave the button doing nothing
    /// with no way to tell why.
    /// </remarks>
    public async Task<SkillReportRecord?> WriteTopicReportAsync(
        long sectionId,
        string topicName,
        TopicConfidence topic,
        CancellationToken ct = default)
    {
        var existing = await _tutor.GetSkillReportAsync(sectionId, ct).ConfigureAwait(false);

        var decision = await _budget.CheckAsync(TutorCallType.SkillReport, ct).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            RaiseStatus(new TutorStatus(decision.Reason ?? "Budget reached."));
            return existing;
        }

        var result = await _client.GenerateSkillReportAsync(topicName, topic, ct).ConfigureAwait(false);
        await LogUsageAsync(TutorCallType.SkillReport, result.Model, result.Usage, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(result.Content))
        {
            return existing;
        }

        var report = new SkillReportRecord
        {
            SectionId = sectionId,
            Content = result.Content.Trim(),
            AttemptsAtGeneration = topic.TotalAttempts,
            Model = result.Model,
            CreatedAt = _clock.UtcNow,
        };

        await _tutor.SaveSkillReportAsync(report, ct).ConfigureAwait(false);
        return report;
    }

    private Task LogUsageAsync(TutorCallType callType, string model, TokenUsage usage, CancellationToken ct) =>
        _usage.LogAsync(
            new ApiUsageLog
            {
                CallType = callType,
                Model = model,
                TokensIn = usage.TokensIn,
                TokensOut = usage.TokensOut,
                // Already read above by PricingTable.Estimate to compute a blended cost;
                // persisting them too is what lets the usage panel show a cache hit
                // actually happened, instead of the split being computed once and thrown away.
                TokensCached = usage.TokensCached,
                TokensCacheWrite = usage.TokensCacheWrite,
                CostEstimate = PricingTable.Estimate(model, usage),
                CreatedAt = _clock.UtcNow,
            }, ct);

    private void RaiseStatus(TutorStatus status) => StatusChanged?.Invoke(this, status);

    /// <summary>
    /// Keeps the live focus window around recent writing. Unbounded unions grow to the
    /// whole page and cause settled mistakes to be re-flagged on every pause.
    /// </summary>
    private static NormalizedRegion CapFocusRegion(NormalizedRegion region)
    {
        if (region.IsEmpty)
        {
            return region;
        }

        const double maxW = 0.42;
        const double maxH = 0.28;
        if (region.Width <= maxW && region.Height <= maxH)
        {
            return region;
        }

        var cx = region.X + (region.Width * 0.5);
        var cy = region.Y + (region.Height * 0.5);
        var w = Math.Min(region.Width, maxW);
        var h = Math.Min(region.Height, maxH);
        return NormalizedRegion.Clamped(cx - (w * 0.5), cy - (h * 0.5), w, h);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }
    }
}
