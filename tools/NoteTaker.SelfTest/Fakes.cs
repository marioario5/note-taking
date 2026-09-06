using System.IO;
using System.Net;
using System.Net.Http;
using NoteTaker.Core.Abstractions;
using NoteTaker.Core.Models;
using NoteTaker.Core.Tutor;

namespace NoteTaker.SelfTest;

public sealed class TestClock(DateTimeOffset start) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = start;

    public void Advance(TimeSpan by) => UtcNow += by;
}

public sealed class TestConnectivity(bool online = true) : IConnectivity
{
    public bool IsOnline { get; set; } = online;

    public event EventHandler<bool>? ConnectivityChanged;

    public void Raise() => ConnectivityChanged?.Invoke(this, IsOnline);
}

public sealed class CountingTutorClient : ITutorClient
{
    public int ScanCalls { get; private set; }

    public int ChatCalls { get; private set; }

    public int SummaryCalls { get; private set; }

    public int PracticeCalls { get; private set; }

    public int WeaknessReviewCalls { get; private set; }

    public IReadOnlyList<TutorRegionFinding> NextFindings { get; set; } =
    [
        new(new NormalizedRegion(0.2, 0.3, 0.3, 0.05), FeedbackSeverity.Major, "Sign error"),
    ];

    public Task<TutorScanResult> ScanPageAsync(TutorScanRequest request, CancellationToken ct = default)
    {
        ScanCalls++;
        return Task.FromResult(new TutorScanResult(
            NextFindings,
            "Check the second line.",
            new TokenUsage(800, 60),
            "test-vision"));
    }

    /// <summary>The most recent chat request, so a test can see what was actually sent.</summary>
    public TutorChatRequest? LastChatRequest { get; private set; }

    public Task<TutorChatResult> ContinueThreadAsync(
        TutorChatRequest request,
        Action<string>? onDelta = null,
        CancellationToken ct = default)
    {
        ChatCalls++;
        LastChatRequest = request;
        return Task.FromResult(new TutorChatResult(
            "What happens to the sign when you distribute?",
            new TokenUsage(400, 40),
            "test-chat"));
    }

    public Task<TutorChatResult> SummarizePatternsAsync(
        IReadOnlyList<TutorFeedback> feedback,
        CancellationToken ct = default)
    {
        SummaryCalls++;
        return Task.FromResult(new TutorChatResult(
            $"You dropped a negative sign {feedback.Count} time(s).",
            new TokenUsage(200, 80),
            "test-chat"));
    }

    public Task<TutorChatResult> GeneratePracticeAsync(
        PracticeGenerationRequest request,
        CancellationToken ct = default)
    {
        PracticeCalls++;
        return Task.FromResult(new TutorChatResult(
            "1. Solve for x…",
            new TokenUsage(1200, 500),
            "test-vision"));
    }

    public int SkillReportCalls { get; private set; }

    /// <summary>How many times the page was marked for Review without tutoring.</summary>
    public int SkillCheckCalls { get; private set; }

    /// <summary>What the next mark should return. Null means the page gave nothing to judge.</summary>
    public SkillVerdict? NextSkillCheck { get; set; } =
        new("u substitution", SkillOutcome.Right, "clean substitution");

    public Task<TutorChatResult> CheckSkillAsync(byte[]? pagePng, CancellationToken ct = default)
    {
        SkillCheckCalls++;
        return Task.FromResult(new TutorChatResult(
            string.Empty,
            new TokenUsage(300, 20),
            "fake-model",
            NextSkillCheck));
    }

    public Task<TutorChatResult> GenerateSkillReportAsync(
        string topicName,
        TopicConfidence topic,
        CancellationToken ct = default)
    {
        SkillReportCalls++;
        return Task.FromResult(new TutorChatResult(
            $"You have worked {topic.TotalAttempts} problems in {topicName}.",
            new TokenUsage(320, 240),
            "fake-model"));
    }

    public Task<TutorChatResult> GenerateWeaknessReviewAsync(
        IReadOnlyList<TutorFeedback> findings,
        IReadOnlyList<WeaknessTopic> recurringTopics,
        CancellationToken ct = default)
    {
        WeaknessReviewCalls++;
        return Task.FromResult(new TutorChatResult(
            $"Let's revisit {findings[0].Label}. What happens to the sign here?",
            new TokenUsage(150, 60),
            "test-chat"));
    }
}

public sealed class StubSnapshotProvider : IPageSnapshotProvider
{
    public int Revision { get; set; } = 1;

    /// <summary>The captured area; defaults to the whole sheet so findings map through unchanged.</summary>
    public NormalizedRegion Area { get; set; } = PageSnapshotCapture.WholeSheet;

    /// <summary>The focus this stub was actually called with, for assertions.</summary>
    public NormalizedRegion? LastFocus { get; private set; }

    public Task<PageSnapshotCapture?> CaptureAsync(
        long pageId,
        NormalizedRegion? focus = null,
        CancellationToken ct = default)
    {
        LastFocus = focus;
        return Task.FromResult<PageSnapshotCapture?>(new PageSnapshotCapture([1, 2, 3, 4], Revision, Area));
    }

    public Task<byte[]?> CaptureRegionAsync(
        long pageId,
        NormalizedRegion region,
        CancellationToken ct = default) =>
        Task.FromResult<byte[]?>([5, 6, 7]);

    /// <summary>The picture the page renders to. Change it to stand for the student writing.</summary>
    public byte[]? ChatPng { get; set; } = [8, 9, 10];

    public Task<byte[]?> CaptureChatContextAsync(
        long pageId,
        NormalizedRegion? focus = null,
        CancellationToken ct = default) =>
        Task.FromResult(ChatPng);

    public bool RegionHasInk { get; set; } = true;

    public Task<bool> RegionContainsInkAsync(
        long pageId,
        NormalizedRegion region,
        InkHitTolerance tolerance = InkHitTolerance.Loose,
        CancellationToken ct = default) =>
        Task.FromResult(RegionHasInk);

    /// <summary>
    /// What snapping should return. Null (the default) means "no plausible line", which keeps
    /// the existing accept-or-expand behaviour — so every test written before snapping existed
    /// still exercises the path it was written for.
    /// </summary>
    public NormalizedRegion? SnapResult { get; set; }

    public Task<NormalizedRegion?> SnapToInkAsync(
        long pageId,
        NormalizedRegion modelBox,
        string? whereHint,
        CancellationToken ct = default) =>
        Task.FromResult(SnapResult);
}

/// <summary>
/// Captures the outgoing request body instead of making a real HTTP call, so transport-level
/// JSON shaping (which fields get sent to which provider) can be asserted deterministically.
/// </summary>
public sealed class RecordingHttpMessageHandler(string responseJson) : HttpMessageHandler
{
    public string? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json"),
        };
    }
}

/// <summary>
/// Serves a canned SSE body a few bytes at a time, so every frame is guaranteed to straddle
/// read boundaries.
/// </summary>
/// <remarks>
/// This is the condition a naive parser gets wrong: read the socket, treat whatever arrived as
/// a complete message, and half a JSON frame silently becomes a dropped token. A MemoryStream
/// would hand the whole body over in one read and prove nothing, so this dribbles.
///
/// Optionally answers the first request with an error status, to exercise the
/// streaming-unsupported fallback without needing a second handler.
/// </remarks>
public sealed class DribblingSseHandler(
    string sseBody,
    int bytesPerRead = 7,
    HttpStatusCode firstStatus = HttpStatusCode.OK,
    string errorBody = "unsupported") : HttpMessageHandler
{
    private int _requests;

    public int Requests => _requests;

    /// <summary>Whether the most recent request asked for a stream.</summary>
    public bool LastRequestedStream { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // Two providers signal streaming differently: OpenAI puts "stream": true in the body,
        // Gemini's native API puts :streamGenerateContent?alt=sse in the URL. Checking only the
        // body silently served the non-streaming branch to the Gemini transport, which then
        // produced an empty reply — a fake that lies is worse than no fake.
        LastRequestedStream = body?.Contains("\"stream\":true") == true
            || body?.Contains("\"stream\": true") == true
            || request.RequestUri?.ToString().Contains("streamGenerateContent") == true;

        var attempt = Interlocked.Increment(ref _requests);
        if (attempt == 1 && firstStatus != HttpStatusCode.OK)
        {
            return new HttpResponseMessage(firstStatus)
            {
                Content = new StringContent(errorBody, System.Text.Encoding.UTF8, "application/json"),
            };
        }

        // The fallback path re-sends without streaming and expects ordinary JSON back.
        if (!LastRequestedStream)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"content":"fallback reply"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2}}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(sseBody);
        var content = new StreamContent(new DribbleStream(bytes, bytesPerRead));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class DribbleStream(byte[] data, int maxPerRead) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = data.Length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            var take = Math.Min(Math.Min(count, maxPerRead), remaining);
            Array.Copy(data, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
