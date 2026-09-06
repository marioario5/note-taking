using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NoteTaker.AI;
using NoteTaker.AI.Transport;
using NoteTaker.App.Controls;
using NoteTaker.App.Services;
using NoteTaker.Core.Expressions;
using NoteTaker.Core.Models;
using NoteTaker.Core.Search;
using NoteTaker.Core.Tutor;
using NoteTaker.Data;
using NoteTaker.Data.Repositories;

namespace NoteTaker.SelfTest;

/// <summary>
/// Runnable gate checks for the phases that are hard to eyeball: stroke fidelity through
/// SQLite, the promise that Practice mode never calls the network, and the cost caps.
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    [STAThread]
    private static int Main()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"notetaker_selftest_{Guid.NewGuid():N}.db");

        try
        {
            RunAsync(databasePath).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nHarness crashed: {ex}");
            _failed++;
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    File.Delete(databasePath + suffix);
                }
                catch (IOException)
                {
                    // The temp file will be cleaned up by the OS.
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task RunAsync(string databasePath)
    {
        var database = new NoteDatabase(databasePath);
        await database.InitializeAsync();

        var notebooks = new NotebookRepository(database);
        var pages = new PageRepository(database);
        var ink = new InkRepository(database);
        var tutorRepo = new TutorRepository(database);
        var usage = new UsageRepository(database);
        var embeddings = new EmbeddingRepository(database);
        var search = new SearchService(embeddings);

        var notebook = await notebooks.CreateNotebookAsync("Self test");
        var section = await notebooks.CreateSectionAsync(notebook.Id, "Gate checks");

        Section("Phase 1 — storage and ink fidelity");
        await TestSchemaSurvivesAStampedDownVersionAsync();
        await TestHundredPageRoundTripAsync(pages, ink, section.Id);
        TestRegionMath();
        TestRenderingAndExport();
        TestPastedImageStaysOutOfScans();
        TestOffSheetWorkIsNotCropped();
        TestCropKeepsWholeMarks();
        TestChatRenderKeepsInkAtFullSize();
        TestLassoContainment();
        TestNeighbouringMarkJoinsScanWindow();
        TestChatHistoryWindow();
        TestSkillVerdictParser();
        TestSkillVerdictParserTolerance();
        TestSkillNamesNormalise();
        TestAttemptsCollapseAConversation();
        TestAttemptsSplitOnALongGap();
        TestConfidenceMeasuresIndependenceAndGrowth();
        TestSkillConfidenceDecay();
        TestSkillConfidenceEmptyState();
        TestBudgetDayIsPacific();
        TestMonthlyBudgetRedistributes();
        TestDailyCeilingIsHard();
        TestUsageOutlook();
        TestHintLadderResets();
        TestReportGateHoldsTheLine();
        TestPerCallTypeOutputBudgets();
        TestInkLineSnapping();

        Section("Phase 2 — tutor behaviour");
        await TestPracticeModeMakesNoCallsAsync(database, ink, tutorRepo, usage);
        await TestLiveModeDebouncesAndCallsAsync(database, ink, tutorRepo, usage);
        await TestEmptyFindingsPrunedOnEraseAsync(database, ink, tutorRepo, usage);
        await TestFixedMistakeClearsInPlaceAsync(database, ink, tutorRepo, usage);
        await TestDistantMistakeSurvivesUnrelatedScanAsync(database, ink, tutorRepo, usage);
        await TestChatFollowsFixedMistakeToNextFindingAsync(database, ink, tutorRepo, usage);
        await TestOfflineQueueAsync(database, ink, tutorRepo, usage);
        await TestReviewOpensHolisticSessionAsync(database, ink, tutorRepo, usage);
        await TestFindingsMapBackFromOffSheetCaptureAsync(database, ink, tutorRepo, usage);
        await TestBudgetCapsAsync(database, usage);
        TestScanParser();
        TestChatAnchorResolver();
        await TestOpenAiTransportCacheFieldsAsync();
        await TestTutorClientCachingCoverageAsync();
        await TestContinueThreadCachesHistoryAsync();
        await TestStreamingTransportAsync();
        await TestReasoningEffortWiringAsync();
        await TestChatReplyCarriesVerdictAndHidesTagAsync();
        await TestStreamedReplyNeverShowsTheTagAsync();
        await TestSkillEventsPersistPerTopicAsync(tutorRepo);
        await TestReviewScoresStoredHistoryAsync(tutorRepo);
        await TestReportSurvivesAndSupersedesAsync(tutorRepo);
        await TestVisionOffSpendsNothingAsync(ink, tutorRepo, usage);
        await TestUnchangedPageSendsNoImageAsync(ink, tutorRepo, usage);
        await TestMarkingRecordsWithoutTutoringAsync(ink, tutorRepo, usage);
        await TestGeminiTransportAsync();
        await TestGeminiStreamingAsync();

        Section("Phase 3 — search");
        await TestSearchAsync(pages, embeddings, search, section.Id);

        Section("Phase 4 — tools");
        TestExpressionEvaluator();

        Section("Prompts");
        TestPromptsLoad();
        TestSyllabusParsing();
        TestRealSyllabusShape();
        TestLessonsSortByTheirNumber();
        TestFindingDeduper();
        TestWeaknessAggregator();
    }

    /// <summary>
    /// Reproduces a real incident: an out-of-date build was launched against a current
    /// database, and on the way out stamped user_version back down to its own, older version.
    /// No table and no row was touched — but the current build then re-entered the migration
    /// ladder at the bottom and threw on the first ALTER TABLE ... ADD COLUMN, so it could not
    /// start, and could not restamp the version that would have let it start. Climbing a rung
    /// twice has to be survivable, or a wrong integer is unrecoverable from inside the app.
    /// </summary>
    private static async Task TestSchemaSurvivesAStampedDownVersionAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"notetaker_downgrade_{Guid.NewGuid():N}.db");
        try
        {
            var database = new NoteDatabase(path);
            await database.InitializeAsync();

            var notebooks = new NotebookRepository(database);
            var notebook = await notebooks.CreateNotebookAsync("Calc 3");
            var section = await notebooks.CreateSectionAsync(notebook.Id, "5.6 Center of Mass");
            var pages = new PageRepository(database);
            await pages.CreatePageAsync(section.Id, "Page 1");

            await using (var connection = await database.OpenAsync())
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version=1;";
                await command.ExecuteNonQueryAsync();
            }

            var reopened = new NoteDatabase(path);
            var started = true;
            try
            {
                await reopened.InitializeAsync();
            }
            catch (Exception ex)
            {
                started = false;
                Console.WriteLine($"        {ex.Message}");
            }

            Check(
                "An older build's version stamp does not lock the app out of its own database",
                started,
                "InitializeAsync threw on a re-run of the migration ladder");

            if (!started)
            {
                return;
            }

            await using (var connection = await reopened.OpenAsync())
            {
                await using var version = connection.CreateCommand();
                version.CommandText = "PRAGMA user_version;";
                var stamped = Convert.ToInt32(await version.ExecuteScalarAsync());

                Check(
                    "Climbing the ladder again restamps the true schema version",
                    stamped == NoteDatabase.SchemaVersion,
                    $"user_version={stamped}, expected {NoteDatabase.SchemaVersion}");
            }

            var survivors = await pages.GetPagesAsync(section.Id);
            Check(
                "Re-running the ladder over a populated database loses nothing",
                survivors.Count == 1 && survivors[0].Title == "Page 1",
                $"{survivors.Count} page(s) survived");
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    File.Delete(path + suffix);
                }
                catch (IOException)
                {
                    // Temp file; the OS will clean it up.
                }
            }
        }
    }

    private static async Task TestHundredPageRoundTripAsync(
        PageRepository pages,
        InkRepository ink,
        long sectionId)
    {
        var random = new Random(20260804);
        var expected = new Dictionary<long, StrokeCollection>();

        for (var i = 0; i < 100; i++)
        {
            var page = await pages.CreatePageAsync(sectionId, $"Round trip {i}");
            var strokes = BuildStrokes(random, strokeCount: random.Next(3, 25));

            using var stream = new MemoryStream();
            strokes.Save(stream);
            await ink.SaveAsync(page.Id, stream.ToArray());

            expected[page.Id] = strokes;
        }

        var lostStrokes = 0;
        var movedPoints = 0;

        foreach (var (pageId, original) in expected)
        {
            var stored = await ink.LoadAsync(pageId);
            if (stored is null)
            {
                lostStrokes += original.Count;
                continue;
            }

            var reloaded = new StrokeCollection(new MemoryStream(stored.IsfBlob));

            if (reloaded.Count != original.Count)
            {
                lostStrokes += Math.Abs(original.Count - reloaded.Count);
                continue;
            }

            for (var s = 0; s < original.Count; s++)
            {
                var a = original[s].StylusPoints;
                var b = reloaded[s].StylusPoints;

                if (a.Count != b.Count)
                {
                    movedPoints += Math.Abs(a.Count - b.Count);
                    continue;
                }

                for (var p = 0; p < a.Count; p++)
                {
                    // ISF quantizes to a device-independent grid; a sub-HIMETRIC unit of
                    // drift is expected and invisible.
                    if (Math.Abs(a[p].X - b[p].X) > 0.05 || Math.Abs(a[p].Y - b[p].Y) > 0.05)
                    {
                        movedPoints++;
                    }
                }
            }
        }

        Check("100 pages save and load with zero stroke loss", lostStrokes == 0, $"{lostStrokes} strokes lost");
        Check("Stroke geometry survives the ISF round trip", movedPoints == 0, $"{movedPoints} points drifted");

        var all = await pages.GetAllPagesAsync();
        Check("All pages are enumerable after write", all.Count >= 100, $"only {all.Count} pages");
    }

    private static StrokeCollection BuildStrokes(Random random, int strokeCount)
    {
        var strokes = new StrokeCollection();

        for (var s = 0; s < strokeCount; s++)
        {
            var points = new StylusPointCollection();
            var x = random.NextDouble() * 1000;
            var y = random.NextDouble() * 1600;

            for (var p = 0; p < random.Next(8, 60); p++)
            {
                x += (random.NextDouble() - 0.5) * 12;
                y += (random.NextDouble() - 0.5) * 12;
                points.Add(new StylusPoint(
                    PageGeometry.OriginX + Math.Clamp(x, 0, PageGeometry.Width),
                    PageGeometry.OriginY + Math.Clamp(y, 0, PageGeometry.Height)));
            }

            strokes.Add(new Stroke(points));
        }

        return strokes;
    }

    private static void TestRegionMath()
    {
        var region = NormalizedRegion.Clamped(0.9, 0.9, 0.5, 0.5);
        Check(
            "Regions stay inside the page when clamped",
            region.X + region.Width <= 1.0001 && region.Y + region.Height <= 1.0001,
            $"{region}");

        var union = new NormalizedRegion(0.1, 0.1, 0.2, 0.2)
            .Union(new NormalizedRegion(0.5, 0.5, 0.1, 0.1));
        Check(
            "Union covers both inputs",
            Math.Abs(union.X - 0.1) < 1e-9 && Math.Abs(union.Width - 0.5) < 1e-9,
            $"{union}");

        // Inflate used to clamp to the sheet, and this asserted that. It now deliberately
        // does not: these are fractions OF the sheet on a canvas that extends well past it,
        // so work below the page is honestly at y > 1 and pulling it back to 1 would put a
        // highlight where the student never wrote. Clamped() is still the way to force a
        // region onto the page when that is genuinely what a caller wants — asserted below.
        var inflated = new NormalizedRegion(0.0, 0.0, 0.1, 0.1).Inflate(0.05);
        Check(
            "Inflate grows a region without pinning it to the page",
            Math.Abs(inflated.X - -0.05) < 1e-9 && Math.Abs(inflated.Width - 0.2) < 1e-9,
            $"{inflated}");

        var forcedOnPage = NormalizedRegion.Clamped(-0.05, -0.05, 0.2, 0.2);
        Check(
            "Clamped still keeps a region on the page",
            forcedOnPage.X >= 0 && forcedOnPage.Y >= 0,
            $"{forcedOnPage}");

        // Normalized coordinates are resolution-free, so a highlight lands on the same ink
        // whether the page is drawn at 100% or 200%.
        var source = new NormalizedRegion(0.25, 0.4, 0.3, 0.06);
        var at100 = (source.X * 1240, source.Y * 1754);
        var at200 = (source.X * 2480 / 2, source.Y * 3508 / 2);
        Check(
            "Highlights map to the same ink at any zoom",
            Math.Abs(at100.Item1 - at200.Item1) < 1e-9 && Math.Abs(at100.Item2 - at200.Item2) < 1e-9,
            "zoom mismatch");
    }

    /// <summary>
    /// Covers the whole raster path on this machine's architecture: WPF rendering, our
    /// PDF writer, and PDFium reading the result back. This is where an ARM64 native
    /// dependency would fail if it were going to.
    /// </summary>
    /// <summary>
    /// Work that runs off the edge of the A4 sheet must still reach the tutor whole.
    /// </summary>
    /// <remarks>
    /// This has now been broken twice by two different mechanisms, which is why it is a test
    /// and not a comment. First an explicit Rect.Intersect(SheetBounds); then, after that was
    /// removed, ToNormalized quietly re-applied the same clipping inside
    /// NormalizedRegion.Clamped (width capped at 1 - left). Both times the failure was
    /// invisible from the call site and visible only in a saved crop: a line running past the
    /// right edge arrived with its tail sliced off, and the tutor objected that it had been
    /// handed half an expression.
    /// </remarks>
    private static void TestOffSheetWorkIsNotCropped()
    {
        // One horizontal stroke starting inside the sheet and running well past its right
        // edge — the shape of a set-builder definition written into open canvas.
        var points = new StylusPointCollection();
        for (var i = 0; i <= 40; i++)
        {
            points.Add(new StylusPoint(
                PageGeometry.OriginX + (PageGeometry.Width * 0.6) + (i * 20),
                PageGeometry.OriginY + (PageGeometry.Height * 0.3)));
        }

        var strokes = new StrokeCollection { new Stroke(points) };

        var focus = new NormalizedRegion(0.6, 0.28, 0.2, 0.05);
        var region = PageRenderer.InkContentRegion(strokes, focus);

        Check(
            "A focused crop keeps ink that runs off the right edge of the sheet",
            region is { } r && r.X + r.Width > 1.0,
            region is { } got ? $"right edge at {got.X + got.Width:F3}, clipped to the sheet" : "null");

        var whole = PageRenderer.InkContentRegion(strokes);

        Check(
            "A whole-page crop keeps ink that runs off the right edge of the sheet",
            whole is { } w && w.X + w.Width > 1.0,
            whole is { } got2 ? $"right edge at {got2.X + got2.Width:F3}, clipped to the sheet" : "null");
    }

    /// <summary>
    /// The native Gemini transport: request shape, image detail, streaming, and usage.
    /// </summary>
    /// <remarks>
    /// This transport exists for one field. Gemini prices an image by a flat per-level
    /// allocation rather than by pixels — measured 1,115 / 551 / 275 tokens for High / Medium /
    /// Low on the same capture — and its OpenAI-compatible shim rejects that field outright.
    /// Everything here guards a way that could silently stop paying off, or worse, 400.
    /// </remarks>
    private static async Task TestGeminiTransportAsync()
    {
        const string canned = """
            {"candidates":[{"content":{"parts":[{"text":"ok"}]},"finishReason":"STOP"}],
             "usageMetadata":{"promptTokenCount":2012,"candidatesTokenCount":16,
                              "thoughtsTokenCount":94,"totalTokenCount":2122}}
            """;

        async Task<string> BodyForAsync(ImageDetail detail, bool json = false)
        {
            var handler = new RecordingHttpMessageHandler(canned);
            var transport = new GeminiTransport(
                new HttpClient(handler), "https://example.test/v1beta", () => "key");

            await transport.SendAsync(new LlmRequest(
                "gemini-3.6-flash",
                "system prompt",
                [
                    new LlmMessage("user", "earlier question"),
                    new LlmMessage("assistant", "earlier reply"),
                    new LlmMessage("user", "is this right?", [1, 2, 3]),
                ],
                1500,
                ExpectJson: json,
                ImageDetail: detail));

            return handler.LastRequestBody ?? string.Empty;
        }

        var low = await BodyForAsync(ImageDetail.Low);

        Check(
            "Gemini transport asks for the image detail it was given",
            low.Contains("\"mediaResolution\":\"MEDIA_RESOLUTION_LOW\""),
            low);

        Check(
            "Gemini transport sends the system prompt as systemInstruction",
            low.Contains("\"systemInstruction\"") && low.Contains("system prompt"),
            low);

        // "assistant" is an OpenAI role. Gemini 400s on it, and it is the single easiest thing
        // to get wrong when porting a message list built for the other shape.
        Check(
            "An assistant turn is sent with Gemini's \"model\" role, never \"assistant\"",
            low.Contains("\"role\":\"model\"") && !low.Contains("\"role\":\"assistant\""),
            low);

        Check(
            "An attached image is sent as inline_data",
            low.Contains("\"inline_data\"") && low.Contains("\"mime_type\":\"image/png\""),
            low);

        var high = await BodyForAsync(ImageDetail.High);
        Check(
            "Image detail is per-request, not fixed at construction",
            high.Contains("MEDIA_RESOLUTION_HIGH"),
            high);

        var asJson = await BodyForAsync(ImageDetail.Low, json: true);
        Check(
            "JSON mode maps to responseMimeType for the scan path",
            asJson.Contains("\"responseMimeType\":\"application/json\""),
            asJson);

        // The counterpart guard. The shim rejects mediaResolution in every spelling tried, and
        // an unknown field there is a 400 that takes the whole tutor down — exactly how
        // gemini-3-pro-preview's retirement presented.
        var openAiHandler = new RecordingHttpMessageHandler(
            """{"choices":[{"message":{"content":"ok"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""");
        var openAi = new OpenAiTransport(
            new HttpClient(openAiHandler), "https://example.test", () => "key", "OpenAiCompatible");
        await openAi.SendAsync(new LlmRequest(
            "gpt-5.6-luna", "sys", [new LlmMessage("user", "hi", [1, 2, 3])], 100,
            ImageDetail: ImageDetail.Low));

        Check(
            "The OpenAI transport never emits mediaResolution, whatever detail is requested",
            openAiHandler.LastRequestBody?.Contains("mediaResolution") == false
                && openAiHandler.LastRequestBody?.Contains("media_resolution") == false,
            openAiHandler.LastRequestBody ?? "null");

        // Thinking tokens are billed as output but excluded from candidatesTokenCount. Missing
        // them would let the daily cap see a fraction of real spend.
        var usageHandler = new RecordingHttpMessageHandler(canned);
        var usageTransport = new GeminiTransport(
            new HttpClient(usageHandler), "https://example.test/v1beta", () => "key");
        var usage = await usageTransport.SendAsync(
            new LlmRequest("gemini-3.6-flash", "s", [new LlmMessage("user", "hi")], 100));

        Check(
            "Thinking tokens are billed alongside the visible reply",
            usage.Usage.TokensOut == 16 + 94 && usage.Usage.TokensIn == 2012,
            $"in {usage.Usage.TokensIn}, out {usage.Usage.TokensOut}");
    }

    /// <summary>
    /// Native SSE: different frames from OpenAI's, and no [DONE] — the stream just ends.
    /// </summary>
    private static async Task TestGeminiStreamingAsync()
    {
        const string sse =
            """
            data: {"candidates":[{"content":{"parts":[{"text":"The "}]}}]}

            data: {"candidates":[{"content":{"parts":[{"text":"setup "}]}}]}

            data: {"candidates":[{"content":{"parts":[{"thought":true,"text":"IGNORE ME"}]}}]}

            data: {"candidates":[{"content":{"parts":[{"text":"is correct."}]}}],"finishReason":"STOP","usageMetadata":{"promptTokenCount":2012,"candidatesTokenCount":9,"thoughtsTokenCount":120,"totalTokenCount":2141}}

            """;

        // 7 bytes per read, so every frame straddles a boundary — the condition a per-chunk
        // parser gets wrong while still producing plausible-looking text.
        var handler = new DribblingSseHandler(sse, bytesPerRead: 7);
        var transport = new GeminiTransport(
            new HttpClient(handler), "https://example.test/v1beta", () => "key");

        var deltas = new List<string>();
        var response = await transport.StreamAsync(
            new LlmRequest("gemini-3.6-flash", "s", [new LlmMessage("user", "hi")], 900),
            deltas.Add);

        Check(
            "Native SSE reassembles frames split across reads",
            response.Content == "The setup is correct.",
            $"got \"{response.Content}\"");

        Check(
            "Every delta is reported as it arrives",
            string.Concat(deltas) == "The setup is correct.",
            string.Concat(deltas));

        // A part flagged "thought" is the model's reasoning. Rendering it would put exactly the
        // deliberation the Socratic prompt forbids into the student's transcript.
        Check(
            "A thought part is billed but never shown",
            !response.Content.Contains("IGNORE ME"),
            response.Content);

        Check(
            "Usage survives streaming, including thinking tokens",
            response.Usage.TokensOut == 9 + 120 && response.Usage.TokensIn == 2012,
            $"in {response.Usage.TokensIn}, out {response.Usage.TokensOut}");
    }

    /// <summary>
    /// With page checking off, nothing may reach the vision model — by any route.
    /// </summary>
    /// <remarks>
    /// The point of turning it off is the bill, so a single missed path defeats it entirely.
    /// There are three ways a scan can start (a stroke, the Review button, and replaying the
    /// offline queue on reconnect), and the queue is the easy one to forget: it would have
    /// spent whatever it had accumulated the next time the app came online.
    /// </remarks>
    /// <summary>
    /// The same picture is not sent twice in a row.
    /// </summary>
    /// <remarks>
    /// The image is the single most expensive part of a chat turn — measured at 275 tokens on
    /// Low against a ~1,050-token prompt — and a follow-up about work already on screen used to
    /// re-send it byte for byte. The model is told the page is unchanged, because otherwise it
    /// announces it cannot see the work and asks the student to describe what it already has.
    /// </remarks>
    private static async Task TestUnchangedPageSendsNoImageAsync(
        InkRepository ink,
        TutorRepository tutorRepo,
        UsageRepository usage)
    {
        const long pageId = 93;

        var client = new CountingTutorClient();
        var snapshots = new StubSnapshotProvider();
        using var coordinator = BuildCoordinator(client, snapshots, ink, tutorRepo, usage);

        var thread = await tutorRepo.GetOrCreateThreadAsync(pageId, null, "image de-duplication");

        await coordinator.AskAsync(pageId, thread.Id, "how does this look?", null);
        Check(
            "The first turn carries the page",
            client.LastChatRequest is { RegionCropPng: not null, PageUnchanged: false },
            "the opening turn sent no image");

        await coordinator.AskAsync(pageId, thread.Id, "and the next step?", null);
        Check(
            "A follow-up on unchanged work sends no image",
            client.LastChatRequest is { RegionCropPng: null, PageUnchanged: true },
            "the same picture was sent twice");

        // The student writes something.
        snapshots.ChatPng = [11, 12, 13, 14];
        await coordinator.AskAsync(pageId, thread.Id, "is this right?", null);
        Check(
            "New ink sends the page again",
            client.LastChatRequest is { RegionCropPng: not null, PageUnchanged: false },
            "fresh work was never shown to the tutor");
    }

    /// <summary>
    /// Marking banks a data point and says nothing.
    /// </summary>
    /// <remarks>
    /// Practising without wanting help is normal, and doing it through the chat paid for a
    /// lesson nobody read to get one row Review could use.
    /// </remarks>
    private static async Task TestMarkingRecordsWithoutTutoringAsync(
        InkRepository ink,
        TutorRepository tutorRepo,
        UsageRepository usage)
    {
        const long pageId = 94;
        const long sectionId = 77;

        var client = new CountingTutorClient();
        using var coordinator = BuildCoordinator(client, new StubSnapshotProvider(), ink, tutorRepo, usage);

        var before = (await tutorRepo.GetSkillEventsAsync(sectionId, DateTimeOffset.MinValue)).Count;
        var verdict = await coordinator.RecordAttemptAsync(pageId, sectionId);
        var after = await tutorRepo.GetSkillEventsAsync(sectionId, DateTimeOffset.MinValue);

        Check(
            "Marking records the attempt against the topic",
            verdict is not null && after.Count == before + 1,
            $"{after.Count - before} row(s) written");

        Check(
            "Marking costs no tutoring turn",
            client.ChatCalls == 0 && client.SkillCheckCalls == 1,
            $"{client.ChatCalls} chat call(s), {client.SkillCheckCalls} mark(s)");

        // An unreadable page is a gap in the record, never a guess in it.
        client.NextSkillCheck = null;
        var nothing = await coordinator.RecordAttemptAsync(pageId, sectionId);
        var afterBlank = await tutorRepo.GetSkillEventsAsync(sectionId, DateTimeOffset.MinValue);

        Check(
            "A page with nothing to judge records nothing",
            nothing is null && afterBlank.Count == after.Count,
            "an unreadable page was recorded as a data point");
    }

    /// <summary>A coordinator wired for the cheap cases: no debounce, no cap in the way.</summary>
    private static TutorCoordinator BuildCoordinator(
        CountingTutorClient client,
        StubSnapshotProvider snapshots,
        InkRepository ink,
        TutorRepository tutorRepo,
        UsageRepository usage) =>
        new(
            client,
            snapshots,
            ink,
            tutorRepo,
            usage,
            new TestConnectivity(true),
            new TestClock(DateTimeOffset.UtcNow),
            new TutorOptions
            {
                LiveDebounce = TimeSpan.FromMilliseconds(60),
                MinLiveInterval = TimeSpan.Zero,
                MaxLiveCallsPerHour = 100,
                MonthlyCostCapUsd = 3000m,
            });

    private static async Task TestVisionOffSpendsNothingAsync(
        InkRepository ink,
        TutorRepository tutorRepo,
        UsageRepository usage)
    {
        const long pageId = 91; // its own page, so other cases' findings can't mask a scan

        var client = new CountingTutorClient();
        using var coordinator = new TutorCoordinator(
            client,
            new StubSnapshotProvider(),
            ink,
            tutorRepo,
            usage,
            new TestConnectivity(true),
            new TestClock(DateTimeOffset.UtcNow),
            new TutorOptions
            {
                LiveDebounce = TimeSpan.FromMilliseconds(60),
                MinLiveInterval = TimeSpan.Zero,
                MaxLiveCallsPerHour = 100,
                MonthlyCostCapUsd = 3000m,

                // VisionEnabled deliberately left at its default — that default IS the
                // behaviour under test.
            });

        coordinator.NotifyInkChanged(pageId, TutorMode.Live, new NormalizedRegion(0.2, 0.2, 0.2, 0.1));
        await Task.Delay(300);

        Check(
            "A stroke starts no scan when page checking is off",
            client.ScanCalls == 0,
            $"{client.ScanCalls} scan(s) made");

        var review = await coordinator.ReviewPageAsync(pageId);

        Check(
            "Review starts no scan when page checking is off",
            client.ScanCalls == 0,
            $"{client.ScanCalls} scan(s) made");

        Check(
            "Review says checking is off rather than implying a clean page",
            review.Message.Contains("off", StringComparison.OrdinalIgnoreCase),
            review.Message);

        await tutorRepo.EnqueueJobAsync(
            new TutorJob
            {
                PageId = pageId,
                CallType = TutorCallType.LiveCheck,
                OriginMode = TutorMode.Live,
                CreatedAt = DateTimeOffset.UtcNow,
            });

        var flushed = await coordinator.FlushPendingJobsAsync();

        Check(
            "A queued offline scan is not replayed when page checking is off",
            flushed == 0 && client.ScanCalls == 0,
            $"{flushed} job(s) flushed, {client.ScanCalls} scan(s) made");
    }

    /// <summary>
    /// A wide page must not shrink the ink below the size it was written at.
    /// </summary>
    /// <remarks>
    /// targetWidth divided by a growing area is a downscale in disguise. Widening the crop to
    /// include pasted figures pushed a real capture to 44% scale, and the tutor read "x/2" as
    /// "1/2". Asserted in pixels because that is the unit the failure occurred in.
    /// </remarks>
    private static void TestChatRenderKeepsInkAtFullSize()
    {
        var strokes = BuildStrokes(new Random(5), strokeCount: 4);

        // Deliberately wider than ChatPageWidth's 1000, the way a page with figures beside
        // the working is.
        var area = new Rect(PageGeometry.OriginX, PageGeometry.OriginY, 2270, 1000);

        var shrunk = PageRenderer.RenderAreaPng(
            strokes, null, area, targetWidth: 1000, ocrContrast: true, drawBackground: false);
        var floored = PageRenderer.RenderAreaPng(
            strokes, null, area, targetWidth: 1000, ocrContrast: true, drawBackground: false,
            minScale: 1.0);

        var shrunkWidth = PngWidth(shrunk);
        var flooredWidth = PngWidth(floored);

        Check(
            "Without a floor a wide area downscales the ink",
            shrunkWidth <= 1000,
            $"{shrunkWidth}px — expected the old shrink-to-fit behaviour to be preserved");

        Check(
            "A chat render never draws ink smaller than it was written",
            flooredWidth >= 2270,
            $"{flooredWidth}px for a 2270-unit area — ink rendered at {flooredWidth / 2270.0:P0}");

        // The hard ceiling must still outrank the floor, or a huge page renders unboundedly.
        var huge = new Rect(PageGeometry.OriginX, PageGeometry.OriginY, 9000, 1000);
        var cappedWidth = PngWidth(PageRenderer.RenderAreaPng(
            strokes, null, huge, targetWidth: 1000, ocrContrast: true, drawBackground: false,
            minScale: 1.0));

        Check(
            "MaxRenderPixels still outranks the legibility floor",
            cappedWidth <= 2400,
            $"{cappedWidth}px exceeds the 2400px ceiling");
    }

    /// <summary>Reads width from a PNG's IHDR, big-endian at byte offset 16.</summary>
    private static int PngWidth(byte[] png) =>
        (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];

    /// <summary>
    /// A crop must contain the marks it touches whole, not in part.
    /// </summary>
    /// <remarks>
    /// The real failure this reproduces: the student wrote "x/2 &lt;= y &lt;= 1" as a run of
    /// separate glyph strokes, only the leftmost of which met the focus box. The tutor was
    /// sent the line with its upper bound cropped off, so it spent an exchange insisting the
    /// student re-read a bound that was not in the picture. Whole marks or nothing.
    /// </remarks>
    private static void TestCropKeepsWholeMarks()
    {
        static Stroke Glyph(double x, double y)
        {
            var points = new StylusPointCollection();
            for (var i = 0; i <= 4; i++)
            {
                points.Add(new StylusPoint(
                    PageGeometry.OriginX + x + (i * 4),
                    PageGeometry.OriginY + y + (i * 2)));
            }

            return new Stroke(points);
        }

        // A line of glyphs marching rightwards; the focus box covers only the first.
        var strokes = new StrokeCollection();
        for (var i = 0; i < 8; i++)
        {
            strokes.Add(Glyph(200 + (i * 60), 400));
        }

        var focus = new NormalizedRegion(0.16, 0.22, 0.04, 0.03);
        var region = PageRenderer.InkContentRegion(strokes, focus);

        // The last glyph starts at 200 + 7*60 = 620 and runs to 636.
        var rightmost = (620.0 + 16.0) / PageGeometry.Width;
        Check(
            "A focused crop grows to include the whole line, not the glyphs it happens to touch",
            region is { } r && r.X + r.Width >= rightmost,
            region is { } got
                ? $"crop ends at {got.X + got.Width:F3}, line ends at {rightmost:F3}"
                : "null");

        // A pasted figure overlapping the focus must arrive whole, not as the strip that
        // happened to intersect.
        var images = new List<PageImage>
        {
            new() { X = PageGeometry.OriginX + 150, Y = PageGeometry.OriginY + 300, Width = 700, Height = 260 },
        };

        var withImage = PageRenderer.InkContentRegion(strokes, focus, images);
        var imageRight = (150.0 + 700.0) / PageGeometry.Width;
        var imageTop = 300.0 / PageGeometry.Height;

        Check(
            "A focused crop contains an overlapping pasted image in full",
            withImage is { } wi && wi.X + wi.Width >= imageRight && wi.Y <= imageTop,
            withImage is { } gotImage
                ? $"crop right {gotImage.X + gotImage.Width:F3} vs image {imageRight:F3}, crop top {gotImage.Y:F3} vs image {imageTop:F3}"
                : "null");
    }

    /// <summary>
    /// A picture the student pastes is theirs to ask about, not the tutor's to mark up: the
    /// chat turn must see it, the mistake-flagging scan must not. That used to hold only as
    /// a side effect — Render skipped the background whenever ocrContrast was on — so a
    /// perfectly reasonable refactor of the OCR colours would have started uploading pasted
    /// images to the scan with nothing to catch it. Now it is an explicit flag, and this is
    /// the test that keeps it honest.
    /// </summary>
    private static void TestPastedImageStaysOutOfScans()
    {
        var strokes = BuildStrokes(new Random(11), strokeCount: 6);

        // A solid, unmistakable background: if any of it reaches the render, the average
        // pixel swings hard towards red, which no combination of black ink on white can do.
        var stamp = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 64, 64));
        }

        stamp.Render(visual);
        stamp.Freeze();

        var scan = PageRenderer.RenderPagePng(strokes, stamp, 400, ocrContrast: true, drawBackground: false);
        var chat = PageRenderer.RenderPagePng(strokes, stamp, 400, ocrContrast: true, drawBackground: true);

        Check(
            "The flagging scan renders without the pasted layer",
            !HasRed(scan),
            "red found — a pasted image reached the scan");

        Check(
            "The chat turn renders WITH the pasted layer",
            HasRed(chat),
            "no red — chat cannot see the pasted image it is being asked about");
    }

    /// <summary>
    /// A mark is only retired when a scan that covered it comes back silent. So a mark fixed
    /// from OUTSIDE its own box — a lone integral sign flagged, then the expression written
    /// beside it — is only clearable if the scan window reaches far enough to include it.
    /// </summary>
    private static void TestNeighbouringMarkJoinsScanWindow()
    {
        // Mirrors TutorCoordinator.NeighbouringMarkReach.
        const double reach = 0.05;

        var mark = new NormalizedRegion(0.20, 0.30, 0.04, 0.05);

        // Writing the rest of the expression immediately to the right of the flagged sign.
        var justWrote = new NormalizedRegion(0.26, 0.30, 0.20, 0.05);

        Check(
            "A mark beside the new writing is reached",
            mark.Intersects(justWrote.Inflate(reach)),
            "the flagged sign fell outside the widened window, so nothing could retire it");

        Check(
            "...and is not already covered without widening",
            !mark.Intersects(justWrote),
            "regions overlap on their own — this case would have passed before the fix too");

        Check(
            "Widening reaches the mark",
            justWrote.Union(mark).Intersects(mark),
            "the union failed to cover the mark it was widened for");

        // The far side of the page must stay out of scope, or an untouched mistake would be
        // read as re-examined and cleared.
        var elsewhere = new NormalizedRegion(0.20, 0.80, 0.04, 0.05);

        Check(
            "A mark far from the new writing is left alone",
            !elsewhere.Intersects(justWrote.Inflate(reach)),
            "an unrelated mark was pulled into the window and could be wrongly cleared");
    }

    /// <summary>
    /// Chat history was 82% of measured spend, resent in full every turn. The trim has to
    /// actually shrink the payload AND hold its cut point still between turns — a window that
    /// slides by one every turn changes the prefix every turn and defeats the very prompt
    /// cache it exists alongside.
    /// </summary>
    /// <summary>
    /// The tutor tags each reply with what it just judged, so Review can accumulate a skill
    /// history without a second model call. The tag is machine-readable and must never reach
    /// the student.
    /// </summary>
    private static void TestSkillVerdictParser()
    {
        var (clean, verdict) = SkillVerdictParser.Parse(
            "That's correct.\n\n⟦u-substitution|right|chose u as the exponent⟧");

        Check(
            "A tagged reply yields the verdict the tutor recorded",
            verdict is { Skill: "u substitution", Outcome: SkillOutcome.Right },
            verdict is null ? "no verdict parsed" : $"{verdict.Skill}/{verdict.Outcome}");

        Check(
            "The tag never reaches the student",
            clean == "That's correct.",
            $"rendered as [{clean}]");
    }

    /// <summary>
    /// A tag the tutor got wrong must still never be rendered. Losing the verdict is a missing
    /// data point; showing the student "⟦only-two|parts⟧" is a visible defect in the one
    /// surface they read.
    /// </summary>
    private static void TestSkillVerdictParserTolerance()
    {
        var (untagged, none) = SkillVerdictParser.Parse("That's correct.");
        Check(
            "An untagged reply is returned untouched, with no verdict",
            none is null && untagged == "That's correct.",
            $"[{untagged}] verdict={none}");

        foreach (var bad in new[] { "⟦only-two|parts⟧", "⟦unclosed|wrong|no bracket", "⟦⟧" })
        {
            var (clean, verdict) = SkillVerdictParser.Parse($"Check the sign.\n\n{bad}");
            Check(
                $"A malformed tag is hidden from the student: {bad}",
                !clean.Contains('⟦') && !clean.Contains('⟧') && clean.StartsWith("Check the sign."),
                $"rendered as [{clean}]");
            _ = verdict;
        }
    }

    /// <summary>
    /// The seam the app actually consumes: whatever ContinueThreadAsync returns is what gets
    /// rendered AND what gets stored and resent as history on every later turn. A tag left in
    /// here is both a visible defect and a recurring token cost.
    /// </summary>
    private static async Task TestChatReplyCarriesVerdictAndHidesTagAsync()
    {
        const string canned =
            """
            {"choices":[{"message":{"content":"Check the subtraction.\n\n⟦distributing a minus|wrong|dropped the sign on the second term⟧"}}],
             "usage":{"prompt_tokens":10,"completion_tokens":5}}
            """;

        var transport = new OpenAiTransport(
            new HttpClient(new RecordingHttpMessageHandler(canned)),
            "https://example.test", () => "key", "OpenAiCompatible");
        var client = new TutorClient(transport, transport, new LlmSettings(), new TutorOptions());

        var result = await client.ContinueThreadAsync(new TutorChatRequest(
            1, 1, [], "is this right?", null, null, Socratic: true, UserTurnCount: 1));

        Check(
            "The reply the app renders and stores carries no tag",
            !result.Content.Contains('⟦') && result.Content == "Check the subtraction.",
            $"[{result.Content}]");

        Check(
            "The verdict rides alongside the reply rather than being re-derived",
            result.Verdict is { Skill: "distributing a minus", Outcome: SkillOutcome.Wrong },
            result.Verdict is null ? "no verdict" : result.Verdict.Skill);
    }

    /// <summary>
    /// The tag arrives in the last frames of a stream, so without care the student watches it
    /// type itself out and then vanish when the finished bubble is swapped in. Deltas are the
    /// only thing rendered live, so the suppression has to happen before they are handed over.
    /// </summary>
    private static async Task TestStreamedReplyNeverShowsTheTagAsync()
    {
        // The tag deliberately straddles frames, which is how it will actually arrive.
        const string sse =
            """
            data: {"choices":[{"delta":{"content":"Check the "},"finish_reason":null}]}

            data: {"choices":[{"delta":{"content":"subtraction."},"finish_reason":null}]}

            data: {"choices":[{"delta":{"content":"\n\n⟦distributing"},"finish_reason":null}]}

            data: {"choices":[{"delta":{"content":" a minus|wrong|dropped the sign⟧"},"finish_reason":"stop"}]}

            data: [DONE]

            """;

        var transport = new OpenAiTransport(
            new HttpClient(new DribblingSseHandler(sse, bytesPerRead: 9)),
            "https://example.test", () => "key", "OpenAiCompatible");
        var client = new TutorClient(transport, transport, new LlmSettings(), new TutorOptions());

        var deltas = new List<string>();
        var result = await client.ContinueThreadAsync(
            new TutorChatRequest(1, 1, [], "is this right?", null, null, Socratic: true, UserTurnCount: 1),
            deltas.Add);

        Check(
            "No delta ever carries the tag to the screen",
            deltas.All(d => !d.Contains('⟦') && !d.Contains('⟧')),
            string.Join(" | ", deltas.Where(d => d.Contains('⟦') || d.Contains('⟧'))));

        Check(
            "What was streamed matches what is finally stored",
            string.Concat(deltas).TrimEnd() == result.Content && result.Content == "Check the subtraction.",
            $"streamed [{string.Concat(deltas)}] stored [{result.Content}]");

        Check(
            "The verdict still survives a streamed reply",
            result.Verdict is { Outcome: SkillOutcome.Wrong },
            result.Verdict is null ? "no verdict" : result.Verdict.Skill);
    }

    /// <summary>
    /// Skill history is the whole point of the feature: it has to outlive the app, and it has
    /// to belong to the TOPIC rather than the page, or working the same topic across three
    /// pages reads as three unrelated histories.
    /// </summary>
    private static async Task TestSkillEventsPersistPerTopicAsync(TutorRepository tutorRepo)
    {
        const long calculus56 = 5601;
        const long calculus57 = 5701;
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);

        await tutorRepo.AddSkillEventAsync(new SkillEvent
        {
            SectionId = calculus56, PageId = 1, Skill = "u-substitution",
            Outcome = SkillOutcome.Wrong, Reason = "picked u as the base",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await tutorRepo.AddSkillEventAsync(new SkillEvent
        {
            SectionId = calculus56, PageId = 2, Skill = "u-substitution",
            Outcome = SkillOutcome.Right, Reason = "chose the exponent",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await tutorRepo.AddSkillEventAsync(new SkillEvent
        {
            SectionId = calculus57, PageId = 3, Skill = "triple integrals",
            Outcome = SkillOutcome.Wrong, Reason = "inner limits reversed",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var topic = await tutorRepo.GetSkillEventsAsync(calculus56, start);

        Check(
            "A topic's events accumulate across different pages",
            topic.Count == 2 && topic.All(e => e.Skill == "u-substitution"),
            $"{topic.Count} event(s)");

        Check(
            "One topic's history never leaks into another's",
            topic.All(e => e.SectionId == calculus56),
            "cross-topic leak");

        Check(
            "The verdict and its reason survive the round trip",
            topic.Any(e => e.Outcome == SkillOutcome.Wrong && e.Reason == "picked u as the base")
                && topic.Any(e => e.Outcome == SkillOutcome.Right),
            string.Join(", ", topic.Select(e => $"{e.Outcome}:{e.Reason}")));

        var future = await tutorRepo.GetSkillEventsAsync(calculus56, DateTimeOffset.UtcNow.AddMinutes(5));
        Check(
            "Decay and recency need a time window, so reads are bounded by it",
            future.Count == 0,
            $"{future.Count} event(s) returned from the future");
    }

    /// <summary>
    /// The report is the only part of Review that spends money, and it generates itself when the
    /// page is opened — so the gate in front of it is the whole safety story. Opening Review must
    /// be free, repeatedly, and a topic must never buy a report it has nothing to say about.
    /// </summary>
    private static void TestReportGateHoldsTheLine()
    {
        var now = DateTimeOffset.UtcNow;

        List<SkillEvent> Attempts(int count, string skill, int corrections)
        {
            var events = new List<SkillEvent>();
            for (var attempt = 0; attempt < count; attempt++)
            {
                var at = now.AddDays(-(count - attempt));
                for (var wrong = 0; wrong < corrections; wrong++)
                {
                    events.Add(Judged(skill, SkillOutcome.Wrong, at.AddMinutes(wrong)));
                }

                events.Add(Judged(skill, SkillOutcome.Right, at.AddMinutes(corrections)));
            }

            return events;
        }

        var thin = SkillConfidence.Score(Attempts(2, "double integrals", 1), now);
        Check(
            "A topic below the threshold never buys a report",
            !SkillReportGate.Decide(thin, existing: null).Generate,
            "a thin topic was allowed to spend");

        var rich = new List<SkillEvent>();
        rich.AddRange(Attempts(2, "double integrals", 1));
        rich.AddRange(Attempts(2, "polar coordinates", 0));
        rich.AddRange(Attempts(2, "sign errors", 3));
        var topic = SkillConfidence.Score(rich, now);

        Check(
            "A topic with enough behind it generates its first report",
            topic.HasEnoughData && SkillReportGate.Decide(topic, existing: null).Generate,
            $"enough={topic.HasEnoughData}, attempts={topic.TotalAttempts}");

        var justWritten = new SkillReportRecord
        {
            SectionId = 1,
            Content = "…",
            AttemptsAtGeneration = topic.TotalAttempts,
            CreatedAt = now,
        };

        Check(
            "Re-opening Review with no new work does not spend again",
            !SkillReportGate.Decide(topic, justWritten).Generate,
            "opening the page twice bought two reports");

        var stale = new SkillReportRecord
        {
            SectionId = 1,
            Content = "…",
            AttemptsAtGeneration = topic.TotalAttempts - SkillReportGate.MinimumNewAttempts,
            CreatedAt = now.AddDays(-7),
        };

        Check(
            "Enough new problems since the last report earns a fresh one",
            SkillReportGate.Decide(topic, stale).Generate,
            $"{SkillReportGate.MinimumNewAttempts} new attempts did not trigger a rewrite");

        var barelyStale = new SkillReportRecord
        {
            SectionId = 1,
            Content = "…",
            AttemptsAtGeneration = topic.TotalAttempts - 1,
            CreatedAt = now.AddDays(-7),
        };

        Check(
            "One more problem is not worth rewriting the report for",
            !SkillReportGate.Decide(topic, barelyStale).Generate,
            "a single new attempt triggered a paid rewrite");

        Check(
            "A refusal explains itself, so the panel can say why it is quiet",
            !string.IsNullOrWhiteSpace(SkillReportGate.Decide(thin, existing: null).Reason),
            "no reason given for holding back");
    }

    /// <summary>
    /// The report is cached so that opening Review is free; that only holds if the cache
    /// actually survives a restart and records how much work it was written from. Losing either
    /// turns every visit to the page back into a paid call.
    /// </summary>
    private static async Task TestReportSurvivesAndSupersedesAsync(TutorRepository tutorRepo)
    {
        const long topicId = 7301;
        var now = DateTimeOffset.UtcNow;

        Check(
            "A topic with no report yet reads back as none",
            await tutorRepo.GetSkillReportAsync(topicId) is null,
            "a report appeared before one was written");

        await tutorRepo.SaveSkillReportAsync(new SkillReportRecord
        {
            SectionId = topicId,
            Content = "Your setup is sound; the losses are arithmetic.",
            AttemptsAtGeneration = 6,
            Model = "fake-model",
            CreatedAt = now.AddDays(-1),
        });

        var stored = await tutorRepo.GetSkillReportAsync(topicId);
        Check(
            "A written report survives to be read back",
            stored is { AttemptsAtGeneration: 6 } && stored.Content.StartsWith("Your setup"),
            stored is null ? "nothing stored" : $"{stored.AttemptsAtGeneration} attempts, [{stored.Content}]");

        await tutorRepo.SaveSkillReportAsync(new SkillReportRecord
        {
            SectionId = topicId,
            Content = "The arithmetic has settled down.",
            AttemptsAtGeneration = 12,
            Model = "fake-model",
            CreatedAt = now,
        });

        var replaced = await tutorRepo.GetSkillReportAsync(topicId);
        Check(
            "A newer report replaces the old one rather than joining it",
            replaced is { AttemptsAtGeneration: 12 } && replaced.Content == "The arithmetic has settled down.",
            replaced is null ? "nothing stored" : $"{replaced.AttemptsAtGeneration}: {replaced.Content}");

        Check(
            "One topic's report never leaks into another's",
            await tutorRepo.GetSkillReportAsync(topicId + 1) is null,
            "a neighbouring topic saw this report");
    }

    /// <summary>
    /// Closes the loop the Review panel actually walks: rows out of SQLite, through attempt
    /// segmentation, into the scorer. Timestamps are the risk — they cross the boundary as text,
    /// and an offset lost in the round trip would not fail any read. It would silently merge
    /// separate attempts into one, or split one into many, and poison every count built on them.
    /// </summary>
    private static async Task TestReviewScoresStoredHistoryAsync(TutorRepository tutorRepo)
    {
        const long topicId = 9101;
        var now = DateTimeOffset.UtcNow;

        // Six attempts across three skills, one per day so each is unambiguously its own run.
        // "limits of integration" needs correcting every time; the other two never do.
        async Task Attempt(string skill, int daysAgo, int corrections)
        {
            for (var i = 0; i < corrections; i++)
            {
                await tutorRepo.AddSkillEventAsync(new SkillEvent
                {
                    SectionId = topicId, PageId = 1, Skill = skill,
                    Outcome = SkillOutcome.Wrong, CreatedAt = now.AddDays(-daysAgo).AddMinutes(i),
                });
            }

            await tutorRepo.AddSkillEventAsync(new SkillEvent
            {
                SectionId = topicId, PageId = 1, Skill = skill,
                Outcome = SkillOutcome.Right, CreatedAt = now.AddDays(-daysAgo).AddMinutes(corrections),
            });
        }

        await Attempt("limits of integration", 5, corrections: 3);
        await Attempt("limits of integration", 4, corrections: 2);
        await Attempt("polar coordinates", 3, corrections: 0);
        await Attempt("polar coordinates", 2, corrections: 0);
        await Attempt("double integrals", 1, corrections: 0);
        await Attempt("double integrals", 1, corrections: 0);

        var stored = await tutorRepo.GetSkillEventsAsync(topicId, now.AddDays(-30));
        var topic = SkillConfidence.Score(stored, now);

        Check(
            "A stored topic scores from the database with no further calls",
            topic.HasEnoughData && topic.Skills.Count == 3,
            $"enough={topic.HasEnoughData}, {topic.Skills.Count} skill(s), {topic.TotalAttempts} attempts");

        Check(
            "Turns stored for one attempt do not read as separate attempts",
            topic.Skills.Single(x => x.Skill == "limits of integration").Attempts == 2,
            $"{topic.Skills.Single(x => x.Skill == "limits of integration").Attempts} attempts from 7 rows");

        Check(
            "Review opens on the skill that needed the most help",
            topic.Skills[0].Skill == "limits of integration" && topic.Skills[0].Corrections == 5,
            $"first was {topic.Skills[0].Skill} ({topic.Skills[0].Corrections} corrections)");

        Check(
            "Timestamps survive SQLite intact enough to date the meter",
            topic.Skills.All(x => x.DaysSinceLastPractised is >= 1 and <= 5),
            string.Join(", ", topic.Skills.Select(x => $"{x.Skill}:{x.DaysSinceLastPractised}d")));
    }

    /// <summary>
    /// The budget day is Pacific, not the machine's own zone and not UTC.
    /// </summary>
    /// <remarks>
    /// UTC put the boundary at 5pm the previous afternoon here, so an evening session opened the
    /// next morning already part-spent. Local time fixed that but moves with the laptop: the same
    /// session would land on different days depending on where it was opened.
    /// </remarks>
    private static void TestBudgetDayIsPacific()
    {
        var now = DateTimeOffset.UtcNow;
        var start = BudgetDay.StartOf(now);

        Check(
            "The budget day begins at midnight Pacific",
            TimeZoneInfo.ConvertTime(start, BudgetDay.Zone).TimeOfDay == TimeSpan.Zero,
            $"day began at {TimeZoneInfo.ConvertTime(start, BudgetDay.Zone):HH:mm} Pacific");

        Check(
            "The budget day contains the moment it was asked about",
            start <= now && now - start < TimeSpan.FromHours(25),
            $"start={start:o} now={now:o}");

        // 11pm Pacific belongs to its own day, which is the bug UTC caused.
        var lateEvening = new DateTimeOffset(2026, 9, 2, 23, 0, 0, TimeSpan.FromHours(-7));
        Check(
            "Late-evening work is charged to the evening's own day",
            TimeZoneInfo.ConvertTime(BudgetDay.StartOf(lateEvening), BudgetDay.Zone).Day == 2,
            $"charged to day {TimeZoneInfo.ConvertTime(BudgetDay.StartOf(lateEvening), BudgetDay.Zone).Day}");

        // The month's real length is what makes "eight dollars a month" true in February too.
        var feb = new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.FromHours(-8));
        var aug = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.FromHours(-7));
        Check(
            "A short month is counted as short and a long one as long",
            BudgetDay.DaysLeftInMonth(feb) == 28 && BudgetDay.DaysLeftInMonth(aug) == 31,
            $"February {BudgetDay.DaysLeftInMonth(feb)}, August {BudgetDay.DaysLeftInMonth(aug)}");

        Check(
            "The month starts on its first day",
            TimeZoneInfo.ConvertTime(BudgetDay.StartOfMonth(aug), BudgetDay.Zone).Day == 1,
            "the month began elsewhere");
    }

    /// <summary>
    /// A quiet day feeds the days after it, and a heavy one is paid for by them.
    /// </summary>
    /// <remarks>
    /// This is the whole reason the daily cap is derived rather than fixed. Study is lumpy —
    /// nothing on Tuesday, four hours on Sunday — so a flat allowance both strands Tuesday's
    /// money and cuts Sunday short while the month as a whole is underspent.
    /// </remarks>
    private static void TestMonthlyBudgetRedistributes()
    {
        const decimal cap = 8.00m;

        var evenShare = MonthlyBudget.AllowanceToday(cap, spentThisMonth: 0m, daysLeft: 30);
        Check(
            "A fresh month divides evenly across its days",
            Math.Round(evenShare, 4) == Math.Round(cap / 30, 4),
            $"${evenShare:0.0000} on day one of thirty");

        // Spend nothing on day one, and day two is dividing the same money across fewer days.
        var afterAQuietDay = MonthlyBudget.AllowanceToday(cap, spentThisMonth: 0m, daysLeft: 29);
        Check(
            "Skipping a day raises the days after it",
            afterAQuietDay > evenShare,
            $"${evenShare:0.0000} then ${afterAQuietDay:0.0000}");

        // Spend a whole week's worth in one sitting, and the rest of the month tightens.
        var afterAHeavyDay = MonthlyBudget.AllowanceToday(cap, spentThisMonth: 2.00m, daysLeft: 29);
        Check(
            "A heavy day is paid for by the ones after it",
            afterAHeavyDay < evenShare,
            $"${afterAHeavyDay:0.0000} after spending $2.00");

        Check(
            "A spent month allows nothing further",
            MonthlyBudget.AllowanceToday(cap, spentThisMonth: 8.00m, daysLeft: 12) == 0m,
            "an exhausted month still offered an allowance");

        // Today's own spending comes out of today's share, not tomorrow's.
        var left = MonthlyBudget.RemainingToday(cap, spentThisMonth: 0.20m, spentToday: 0.20m, daysLeft: 30);
        Check(
            "What today has already spent comes out of today's share",
            Math.Round(left, 4) == Math.Round((cap / 30) - 0.20m, 4),
            $"${left:0.0000} left of ${cap / 30:0.0000}");

        Check(
            "A day cannot borrow past the month's own ceiling",
            MonthlyBudget.RemainingToday(cap, spentThisMonth: 7.95m, spentToday: 0m, daysLeft: 1) <= 0.05m,
            "the last day was allowed more than the month had left");
    }

    /// <summary>
    /// The usage screen answers "am I going to run out", not "what happened".
    /// </summary>
    private static void TestUsageOutlook()
    {
        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.FromHours(-7));
        var today = BudgetDay.StartOf(now);

        var spend = new Dictionary<DateTimeOffset, SpendSplit>
        {
            [BudgetDay.StartOf(now.AddDays(-3))] = new SpendSplit(0.40m, 0m, 0m),
            [BudgetDay.StartOf(now.AddDays(-1))] = new SpendSplit(0.10m, 0m, 0m),
            [today] = new SpendSplit(0.05m, 0m, 0m),
        };

        var outlook = UsageOutlook.Build(8.00m, spend, now);

        Check(
            "The strip covers seven days ending today",
            outlook.Week.Count == 7 && outlook.Week[^1].Day == today,
            $"{outlook.Week.Count} days, ending {outlook.Week[^1].Day:d}");

        // The bug this pins: on 5 September the strip reached back to 30 and 31 August, whose
        // allowances were the whole August cap over its last two days — $4.00 and $8.00. Those
        // two bars flattened every day of the current month into an unreadable sliver.
        var early = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.FromHours(-7));
        var clamped = UsageOutlook.Build(8.00m, new Dictionary<DateTimeOffset, SpendSplit>(), early);

        Check(
            "The strip never reaches back past the first of the month",
            clamped.Week.Count == 5
                && clamped.Week.All(d => d.Day >= BudgetDay.StartOfMonth(early)),
            $"{clamped.Week.Count} days, first {clamped.Week[0].Day:d}");

        Check(
            "No day is drawn against a previous month's leftover cap",
            clamped.Week.All(d => d.Allowance <= 8.00m / 26),
            $"largest allowance ${clamped.Week.Max(d => d.Allowance):0.00}");

        Check(
            "Days with no spending are shown as zero rather than missing",
            outlook.Week.Count(d => d.Spent.Total == 0) == 4,
            $"{outlook.Week.Count(d => d.Spent.Total == 0)} empty days");

        // The split is a second reading of the same money, so it must never change the total.
        var mixed = new Dictionary<DateTimeOffset, SpendSplit>
        {
            [today] = default(SpendSplit)
                .Add(TutorCallType.SocraticChat, 0.12m)
                .Add(TutorCallType.SkillReport, 0.30m)
                .Add(TutorCallType.SkillCheck, 0.04m)
                .Add(TutorCallType.LiveCheck, 0.02m),
        };

        var day = mixed[today];
        Check(
            "Chat is filed under the tutor, marking and reports under review",
            day.Tutor == 0.12m && day.Review == 0.34m && day.Other == 0.02m,
            $"tutor ${day.Tutor:0.00}, review ${day.Review:0.00}, other ${day.Other:0.00}");

        Check(
            "Splitting the spend does not change what it adds up to",
            day.Total == 0.48m && UsageOutlook.Build(8.00m, mixed, now).SpentThisMonth == 0.48m,
            $"${day.Total:0.00}");

        Check(
            "The month's spend is the sum of its days",
            outlook.SpentThisMonth == 0.55m,
            $"${outlook.SpentThisMonth:0.00}");

        // September has 30 days, so the flat share is the reference everything is read against.
        Check(
            "The even share is the cap over the month's real length",
            Math.Round(outlook.EvenShare, 4) == Math.Round(8.00m / 30, 4),
            $"${outlook.EvenShare:0.0000}");

        // Ten days in at $0.055/day, the projection lands nowhere near the cap.
        Check(
            "A light month projects under the cap",
            outlook.ProjectedFraction < 0.3,
            $"projected ${outlook.ProjectedMonth:0.00} of ${outlook.MonthlyCap:0.00}");

        Check(
            "Underspending raises what today may spend above the flat share",
            outlook.ShareAgainstEven > 1,
            $"today ${outlook.AllowanceToday:0.0000} against ${outlook.EvenShare:0.0000}");

        // The case the screen exists for: a heavy month that will not reach the end.
        var heavy = new Dictionary<DateTimeOffset, SpendSplit> { [BudgetDay.StartOf(now.AddDays(-1))] = new SpendSplit(6.00m, 0m, 0m) };
        var tight = UsageOutlook.Build(8.00m, heavy, now);

        Check(
            "A heavy month projects past the cap",
            tight.ProjectedFraction > 1,
            $"projected ${tight.ProjectedMonth:0.00}");

        Check(
            "Overspending lowers what today may spend below the flat share",
            tight.ShareAgainstEven < 1,
            $"today ${tight.AllowanceToday:0.0000} against ${tight.EvenShare:0.0000}");

        // The movement the chart exists to draw. Spend nothing for days and the ceiling climbs;
        // spend heavily and the days after it are cut. Drawn as a flat line, none of this shows.
        var quiet = UsageOutlook.Build(8.00m, new Dictionary<DateTimeOffset, SpendSplit>(), now);
        Check(
            "Days that spend nothing raise the cap for the days after them",
            quiet.Week.Zip(quiet.Week.Skip(1)).All(pair => pair.Second.Allowance > pair.First.Allowance),
            "the cap stayed flat across an unused week");

        var spike = UsageOutlook.Build(
            8.00m,
            new Dictionary<DateTimeOffset, SpendSplit> { [BudgetDay.StartOf(now.AddDays(-3))] = new SpendSplit(3.00m, 0m, 0m) },
            now);

        var beforeSpike = spike.Week.First(d => d.Day == BudgetDay.StartOf(now.AddDays(-4))).Allowance;
        var afterSpike = spike.Week.First(d => d.Day == BudgetDay.StartOf(now.AddDays(-2))).Allowance;

        Check(
            "A heavy day lowers the cap for the days after it",
            afterSpike < beforeSpike,
            $"${beforeSpike:0.0000} before the spike, ${afterSpike:0.0000} after");

        // Its own outlay is not held against it: the ceiling a day gets is fixed when the day
        // opens, which is what makes it a ceiling rather than a moving target to chase.
        var withoutSpike = UsageOutlook.Build(8.00m, new Dictionary<DateTimeOffset, SpendSplit>(), now);
        Check(
            "A day's own spending does not lower its own cap",
            spike.Week.First(d => d.Day == BudgetDay.StartOf(now.AddDays(-3))).Allowance
                == withoutSpike.Week.First(d => d.Day == BudgetDay.StartOf(now.AddDays(-3))).Allowance,
            "the heavy day was charged against its own allowance");

        // Priced from real turns, so the figure moves with what questions actually cost.
        var priced = UsageOutlook.Build(8.00m, spend, now, chatTurnCost: 0.006m);
        Check(
            "What is left buys a countable number of questions a day",
            priced.QuestionsPerDayLeft is > 0,
            $"{priced.QuestionsPerDayLeft} questions a day");

        // What the raised cap is worth, not just that it rose.
        Check(
            "A raised cap is worth extra questions a day",
            priced.ExtraQuestionsPerDay is > 0,
            $"{priced.ExtraQuestionsPerDay} extra a day on a ${priced.AllowanceToday:0.0000} cap "
            + $"against ${priced.EvenShare:0.0000}");

        var squeezed = UsageOutlook.Build(
            8.00m,
            new Dictionary<DateTimeOffset, SpendSplit> { [BudgetDay.StartOf(now.AddDays(-1))] = new SpendSplit(4.00m, 0m, 0m) },
            now,
            chatTurnCost: 0.006m);

        Check(
            "A cap cut by heavy spending is worth fewer questions a day",
            squeezed.ExtraQuestionsPerDay is < 0,
            $"{squeezed.ExtraQuestionsPerDay} on a ${squeezed.AllowanceToday:0.0000} cap");

        Check(
            "With nothing asked yet the estimate is withheld rather than guessed",
            UsageOutlook.Build(8.00m, spend, now).QuestionsPerDayLeft is null,
            "an unmeasured question price still produced an estimate");

        Check(
            "What is left of the month is never negative",
            UsageOutlook.Build(8.00m, new Dictionary<DateTimeOffset, SpendSplit>
            {
                [BudgetDay.StartOf(now.AddDays(-1))] = new SpendSplit(99.00m, 0m, 0m),
            }, now).RemainingThisMonth == 0m,
            "an overspent month reported money left");
    }

    /// <summary>
    /// The reveal lock re-arms when the work moves on.
    /// </summary>
    /// <remarks>
    /// The bug this pins down: the ladder counted messages for a whole session, so once the count
    /// passed the reveal threshold on one problem it stayed past it. A fresh "is this correct for
    /// the upper triangle?" — a part the student had only just started — came back with the
    /// corrected antiderivative and the final answer.
    /// </remarks>
    private static void TestHintLadderResets()
    {
        var wrong = new SkillVerdict("u substitution", SkillOutcome.Wrong, "picked the wrong u");

        Check(
            "Still wrong on the same skill keeps the ladder where it is",
            !HintLadder.ShouldReset(wrong, "u substitution"),
            "a stuck student was dropped back to rung one");

        Check(
            "Getting it right sends the next mistake back to rung one",
            HintLadder.ShouldReset(new SkillVerdict("u substitution", SkillOutcome.Right, "correct"), "u substitution"),
            "the count carried over past a solved problem");

        Check(
            "Moving to a different skill resets the ladder",
            HintLadder.ShouldReset(wrong, "limits of integration"),
            "earlier work on another skill still counted toward the reveal");

        Check(
            "Case and spacing don't make it a different skill",
            !HintLadder.ShouldReset(wrong, "U Substitution"),
            "a capitalised tag read as a change of subject");

        Check(
            "The first judged skill of a session is not a change of subject",
            !HintLadder.ShouldReset(wrong, string.Empty),
            "the very first verdict reset a ladder that had not started");

        // A missing tag is the model forgetting, not evidence about the student.
        Check(
            "A missing verdict leaves the ladder alone",
            !HintLadder.ShouldReset(null, "u substitution"),
            "a dropped tag reset the ladder and re-locked a stuck student");

        Check(
            "An unclear verdict on the same skill leaves the ladder alone",
            !HintLadder.ShouldReset(new SkillVerdict("u substitution", SkillOutcome.Unclear, "cannot read"), "u substitution"),
            "an unreadable page reset the ladder");
    }

    /// <summary>
    /// The day's ceiling is a stop, not a suggestion.
    /// </summary>
    /// <remarks>
    /// Redistribution is only a reward for saving if the opposite is bounded. Left uncapped, one
    /// long afternoon eats a week of the month and the student finds out days later, when the
    /// tutor has nothing left to give and nothing can be done about it.
    /// </remarks>
    private static void TestDailyCeilingIsHard()
    {
        const decimal cap = 8.00m;
        const decimal floor = 0.05m;

        var share = MonthlyBudget.AllowanceToday(cap, spentThisMonth: 0m, daysLeft: 30);

        Check(
            "A day that has spent its share has nothing left",
            MonthlyBudget.RemainingToday(cap, share, share, 30, floor) == 0m,
            $"still offered money after spending its whole ${share:0.0000}");

        Check(
            "Overspending one day cannot go further than the ceiling",
            MonthlyBudget.RemainingToday(cap, share * 3, share * 3, 30, floor) == 0m,
            "an already-overspent day was offered more");

        // The floor is a floor, not a licence: it lifts a starved day up, never past the month.
        Check(
            "A starved day is still given the minimum",
            MonthlyBudget.CeilingToday(cap, spentThisMonth: 7.90m, spentToday: 0m, daysLeft: 20, minimumAllowance: floor) == floor,
            "the floor did not apply");

        Check(
            "The floor never exceeds what the month has left",
            MonthlyBudget.CeilingToday(cap, spentThisMonth: 7.99m, spentToday: 0m, daysLeft: 20, minimumAllowance: floor) <= 0.01m,
            "the floor spent money the month did not have");

        // Borrowing is the only way past, and it is bounded by the month too.
        Check(
            "An explicit borrow raises today's ceiling",
            MonthlyBudget.CeilingToday(cap, 0m, 0m, 30, floor, borrowed: 0.20m) > share,
            "the borrow had no effect");

        Check(
            "A borrow cannot cross the month's cap",
            MonthlyBudget.CeilingToday(cap, 7.90m, 0m, 5, floor, borrowed: 5.00m) <= 0.10m,
            "borrowing broke the monthly cap");

        // What the dialog has to say out loud: borrowing costs the days after today.
        var (ifYouStop, ifYouBorrow) = MonthlyBudget.DaysAfterToday(cap, 1.00m, extra: 0.50m, daysLeft: 8);
        Check(
            "Borrowing visibly lowers the days that follow",
            ifYouBorrow < ifYouStop,
            $"${ifYouStop:0.0000} then ${ifYouBorrow:0.0000}");
    }

    /// <summary>
    /// A Socratic dialogue judges the same skill on every turn, so turns are not evidence: one
    /// problem worked through with help produced nine rows for a single skill, and because the
    /// loop only ends once the student is right, "wrong" is over-counted by construction. The
    /// unit has to be the attempt — a contiguous run on one skill — or every meter reads
    /// pessimistic no matter how much data accumulates.
    /// </summary>
    private static void TestAttemptsCollapseAConversation()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-30);
        DateTimeOffset At(int minute) => start.AddMinutes(minute);

        // The student's real first session: two problems, fifteen turns.
        var attempts = SkillAttempts.Segment(
        [
            Judged("triple integrals", SkillOutcome.Right, At(0)),
            Judged("triple integrals", SkillOutcome.Right, At(1)),
            Judged("cylindrical coordinates", SkillOutcome.Unclear, At(2)),
            Judged("cylindrical coordinates", SkillOutcome.Right, At(3)),
            Judged("cylindrical coordinates", SkillOutcome.Wrong, At(4)),
            Judged("cylindrical coordinates", SkillOutcome.Wrong, At(5)),
            Judged("cylindrical coordinates", SkillOutcome.Wrong, At(6)),
            Judged("cylindrical coordinates", SkillOutcome.Wrong, At(7)),
            Judged("cylindrical coordinates", SkillOutcome.Wrong, At(8)),
            Judged("triple integrals", SkillOutcome.Right, At(9)),
            Judged("spherical coordinates", SkillOutcome.Wrong, At(14)),
            Judged("spherical coordinates", SkillOutcome.Right, At(15)),
        ]);

        Check(
            "A run of turns on one skill collapses into a single attempt",
            attempts.Count == 4,
            $"{attempts.Count} attempts from 12 turns: {string.Join(", ", attempts.Select(a => a.Skill))}");

        var cylindrical = attempts.Single(a => a.Skill == "cylindrical coordinates");
        Check(
            "An attempt counts how much help it took, not how it ended",
            cylindrical.Corrections == 5,
            $"{cylindrical.Corrections} corrections");

        Check(
            "Returning to a skill later starts a new attempt",
            attempts.Count(a => a.Skill == "triple integrals") == 2,
            "the two separate triple-integral runs did not split");
    }

    /// <summary>
    /// Leaving a skill and coming back hours later is a fresh attempt even though no other skill
    /// intervened — otherwise a whole week of work on one topic reads as a single data point.
    /// </summary>
    private static void TestAttemptsSplitOnALongGap()
    {
        var now = DateTimeOffset.UtcNow;
        var attempts = SkillAttempts.Segment(
        [
            Judged("line integrals", SkillOutcome.Wrong, now.AddHours(-5)),
            Judged("line integrals", SkillOutcome.Right, now.AddHours(-5).AddMinutes(2)),
            Judged("line integrals", SkillOutcome.Right, now),
        ]);

        Check(
            "A long pause between turns starts a new attempt",
            attempts.Count == 2,
            $"{attempts.Count} attempts");

        Check(
            "An attempt needing no correction scores above one that did",
            attempts[1].Independence > attempts[0].Independence,
            $"{attempts[0].Independence:0.00} then {attempts[1].Independence:0.00}");
    }

    /// <summary>
    /// The point of the meter, in the student's words: "if I am with the tutor everything will
    /// eventually be right". So the score is how INDEPENDENTLY a skill is being exercised, and
    /// the trend is whether that is improving from problem to problem.
    /// </summary>
    private static void TestConfidenceMeasuresIndependenceAndGrowth()
    {
        var now = DateTimeOffset.UtcNow;

        List<SkillEvent> Session(int[] corrections)
        {
            var events = new List<SkillEvent>();
            for (var attempt = 0; attempt < corrections.Length; attempt++)
            {
                var at = now.AddDays(-(corrections.Length - attempt));
                for (var wrong = 0; wrong < corrections[attempt]; wrong++)
                {
                    events.Add(Judged("flux integrals", SkillOutcome.Wrong, at.AddMinutes(wrong)));
                }

                events.Add(Judged("flux integrals", SkillOutcome.Right, at.AddMinutes(corrections[attempt])));
            }

            return events;
        }

        var improving = SkillConfidence.Score(Session([4, 3, 1, 0]), now).Skills.Single();

        Check(
            "Needing less help over time reads as improving",
            improving.Trend == SkillTrend.Improving,
            $"trend was {improving.Trend}");

        Check(
            "Attempts are reported, not raw turns",
            improving.Attempts == 4,
            $"{improving.Attempts} attempts");

        var slipping = SkillConfidence.Score(Session([0, 1, 3, 4]), now).Skills.Single();
        Check(
            "Needing more help over time reads as slipping",
            slipping.Trend == SkillTrend.Slipping,
            $"trend was {slipping.Trend}");

        Check(
            "Working independently scores higher than being walked through it",
            improving.Confidence > slipping.Confidence,
            $"improving {improving.Confidence:0.00} vs slipping {slipping.Confidence:0.00}");
    }

    /// <summary>
    /// Decay pulls a score toward the middle, not downward: time passing is a loss of evidence,
    /// not evidence of a loss. That keeps a stale strength from sitting at the bottom of the list
    /// forever unrevisited, while a stale weakness still outranks anything freshly independent.
    /// </summary>
    private static void TestSkillConfidenceDecay()
    {
        var now = DateTimeOffset.UtcNow;
        var stale = now.AddDays(-90);

        var fresh = SkillConfidence.Score([Judged("gradients", SkillOutcome.Right, now)], now)
            .Skills.Single().Confidence;
        var aged = SkillConfidence.Score([Judged("gradients", SkillOutcome.Right, stale)], now)
            .Skills.Single();

        Check(
            "A stale strength decays toward the middle",
            aged.Confidence < fresh && aged.Confidence > 0.5,
            $"fresh {fresh:0.00} -> aged {aged.Confidence:0.00}");

        Check(
            "Days since last practice are reported for the meter caption",
            aged.DaysSinceLastPractised == 90,
            $"{aged.DaysSinceLastPractised} days");
    }

    /// <summary>
    /// The threshold counts ATTEMPTS, not turns, because turns are cheap to accumulate and say
    /// nothing. The student's own first session — two problems, fifteen turns, four attempts
    /// across three skills — must still report "not enough yet"; three meters drawn from it was
    /// the complaint that produced this rule.
    /// </summary>
    private static void TestSkillConfidenceEmptyState()
    {
        var now = DateTimeOffset.UtcNow;

        Check(
            "A topic with no history at all reports no data",
            !SkillConfidence.Score([], now).HasEnoughData,
            "empty history claimed enough data");

        var firstSession = new List<SkillEvent>();
        void Run(string skill, int minute, params SkillOutcome[] outcomes)
        {
            for (var i = 0; i < outcomes.Length; i++)
            {
                firstSession.Add(Judged(skill, outcomes[i], now.AddMinutes(minute + i)));
            }
        }

        Run("triple integrals", 0, SkillOutcome.Right, SkillOutcome.Right);
        Run("cylindrical coordinates", 2, SkillOutcome.Unclear, SkillOutcome.Right, SkillOutcome.Wrong,
            SkillOutcome.Wrong, SkillOutcome.Wrong, SkillOutcome.Wrong, SkillOutcome.Wrong);
        Run("triple integrals", 9, SkillOutcome.Right);
        Run("spherical coordinates", 14, SkillOutcome.Wrong, SkillOutcome.Right);

        var early = SkillConfidence.Score(firstSession, now);
        Check(
            "Two problems across three skills is still not enough to draw a meter",
            !early.HasEnoughData && early.TotalAttempts == 4,
            $"{early.TotalAttempts} attempts, enough={early.HasEnoughData}");

        var unclearOnly = SkillConfidence.Score([Judged("curl", SkillOutcome.Unclear, now)], now);
        Check(
            "A skill the tutor never actually judged is not a skill",
            unclearOnly.Skills.Count == 0,
            $"{unclearOnly.Skills.Count} skills from one unclear verdict");
    }

    private static SkillEvent Judged(string skill, SkillOutcome outcome, DateTimeOffset at) =>
        new() { SectionId = 1, PageId = 1, Skill = skill, Outcome = outcome, CreatedAt = at };

    /// <summary>
    /// Skill names only accumulate if the same skill produces the same string. A live run
    /// returned both "iterated integrals" and "iterated-integrals" for one skill, which would
    /// have split one history into two and quietly halved every count built on it.
    /// </summary>
    private static void TestSkillNamesNormalise()
    {
        var names = new[]
        {
            "iterated-integrals", "Iterated Integrals", "  iterated   integrals  ",
            "ITERATED_INTEGRALS",
        };

        var parsed = names
            .Select(n => SkillVerdictParser.Parse($"ok\n\n⟦{n}|wrong|why⟧").Verdict!.Skill)
            .Distinct()
            .ToList();

        Check(
            "Spelling variants of one skill collapse to one name",
            parsed.Count == 1 && parsed[0] == "iterated integrals",
            string.Join(" / ", parsed));
    }

    private static void TestChatHistoryWindow()
    {
        var thread = new List<TutorMessage>();
        for (var i = 0; i < 40; i++)
        {
            thread.Add(new TutorMessage
            {
                Id = i,
                Role = i % 2 == 0 ? MessageRole.User : MessageRole.Assistant,
                Content = $"turn {i}",
            });
        }

        var short8 = ChatHistoryWindow.Select(thread.Take(8).ToList());
        Check(
            "A thread under the cap is returned whole",
            short8.Count == 8 && ReferenceEquals(short8[0], thread[0]),
            $"expected all 8 messages untouched, got {short8.Count}");

        // Grow the thread one message at a time and track every cut point chosen. The two
        // properties that matter: the payload actually shrinks once past the cap, and the
        // prefix does not change on every single call — only when a block boundary is
        // crossed. Comparing by reference (not value) is deliberate: it proves the SAME
        // message objects survive across calls, not merely equal-looking ones.
        var prior = thread.Take(ChatHistoryWindow.MaxMessages).ToList();
        var stableRun = 0;
        var sawShrink = false;
        var sawStableAcrossGrowth = false;

        for (var length = ChatHistoryWindow.MaxMessages + 1; length <= thread.Count; length++)
        {
            var window = ChatHistoryWindow.Select(thread.Take(length).ToList());

            if (window.Count < length)
            {
                sawShrink = true;
            }

            // Same cut point as last time: the shared prefix must be the exact same objects,
            // and this turn's window must be that prefix plus exactly the new message.
            if (window.Count == prior.Count + 1)
            {
                var prefixMatches = true;
                for (var i = 0; i < prior.Count; i++)
                {
                    if (!ReferenceEquals(window[i], prior[i]))
                    {
                        prefixMatches = false;
                        break;
                    }
                }

                if (prefixMatches && ReferenceEquals(window[^1], thread[length - 1]))
                {
                    stableRun++;
                    if (stableRun >= ChatHistoryWindow.TrimBlock - 1)
                    {
                        sawStableAcrossGrowth = true;
                    }
                }
            }
            else
            {
                stableRun = 0;
            }

            prior = window.ToList();
        }

        Check(
            "A long thread is trimmed, not resent in full",
            sawShrink,
            "history kept growing past the cap with nothing ever dropped");

        Check(
            "The cut point holds steady across several turns of growth",
            sawStableAcrossGrowth,
            "the retained prefix changed on every turn, which invalidates a prompt cache on every call");
    }

    /// <summary>
    /// One shared 4000-token ceiling used to serve every call type — a two-sentence Socratic
    /// hint and an 8-region Review scan alike. A reply that wandered into unrequested
    /// deliberation had that whole budget to run in before hitting a wall; this checks each
    /// call type actually got its own smaller one instead of silently sharing the old default.
    /// </summary>
    private static void TestPerCallTypeOutputBudgets()
    {
        var settings = new LlmSettings();

        var chat = settings.MaxOutputTokensFor(TutorCallType.SocraticChat);
        var review = settings.MaxOutputTokensFor(TutorCallType.ReviewScan);
        var live = settings.MaxOutputTokensFor(TutorCallType.LiveCheck);
        var practice = settings.MaxOutputTokensFor(TutorCallType.PracticeGeneration);

        Check(
            "Chat gets a far smaller cap than a full-page scan",
            chat < review,
            $"chat={chat}, review={review} — chat should be a few sentences, not scan-sized");

        Check(
            "Chat is not simply the old blanket default",
            chat != settings.MaxOutputTokens,
            $"chat cap ({chat}) still equals the generic MaxOutputTokens ({settings.MaxOutputTokens})");

        Check(
            "A live check needs less room than a thorough review (fewer regions)",
            live < review,
            $"live={live}, review={review}");

        Check(
            "Practice generation keeps the large cap — it writes a whole worksheet",
            practice == settings.MaxOutputTokens,
            $"expected practice generation to keep the general {settings.MaxOutputTokens}-token headroom, got {practice}");

        // An unrecognised call type must still get a usable, non-zero budget rather than
        // silently falling through to nothing.
        var unknown = settings.MaxOutputTokensFor((TutorCallType)999);
        Check(
            "An unrecognised call type falls back to the general default, not zero",
            unknown == settings.MaxOutputTokens && unknown > 0,
            $"fallback returned {unknown}, expected {settings.MaxOutputTokens}");
    }

    /// <summary>
    /// Snapping exists because the model's box is its least reliable output (~0.45 accuracy
    /// against ~0.65 for detection). These check the two things that matter: it lands on real
    /// ink using the coarse hint, and a wrong box cannot drag it off a correct match.
    /// </summary>
    private static void TestInkLineSnapping()
    {
        // Three lines of writing, well separated vertically — top, middle, foot.
        List<NormalizedRegion> lines =
        [
            new(0.10, 0.10, 0.50, 0.04), // "top of page"
            new(0.10, 0.50, 0.50, 0.04), // "middle"
            new(0.10, 0.88, 0.50, 0.04), // "foot of page"
        ];

        Check(
            "Bands are the same words the prompt asks the model for",
            InkLineSnapper.DescribeBand(lines[0]) == "top of page"
                && InkLineSnapper.DescribeBand(lines[1]) == "middle"
                && InkLineSnapper.DescribeBand(lines[2]) == "foot of page",
            $"{InkLineSnapper.DescribeBand(lines[0])} / {InkLineSnapper.DescribeBand(lines[1])} / {InkLineSnapper.DescribeBand(lines[2])}");

        // The case this whole change exists for: the hint is right, the box is wrong. A box
        // sitting in blank space near the foot must not beat the band the model itself named.
        var wrongBox = new NormalizedRegion(0.10, 0.70, 0.30, 0.03);
        var snapped = InkLineSnapper.Snap(lines, wrongBox, "middle");

        Check(
            "A correct band hint wins over a misplaced box",
            snapped is { } s && s.Y < 0.55 && s.Y + s.Height > 0.50,
            snapped?.ToString() ?? "null");

        Check(
            "The snapped region sits on ink rather than in the gap the box pointed at",
            snapped is { } onInk && lines.Any(l => l.Intersects(onInk)),
            snapped?.ToString() ?? "null");

        // With no hint at all, the box is the only signal left and should still be followed.
        var boxOnFoot = new NormalizedRegion(0.15, 0.88, 0.20, 0.04);
        var noHint = InkLineSnapper.Snap(lines, boxOnFoot, whereHint: null);

        Check(
            "With no hint, the box still selects the overlapping line",
            noHint is { } n && n.Y + (n.Height / 2) > 0.8,
            noHint?.ToString() ?? "null");

        // A box far from every line, with no usable hint, is not something to guess about.
        var nowhere = InkLineSnapper.Snap(lines, new NormalizedRegion(0.10, 0.30, 0.10, 0.02), null);

        Check(
            "Nothing plausible nearby returns null so the caller keeps the model's box",
            nowhere is null,
            nowhere?.ToString() ?? "null");

        Check(
            "An empty page cannot be snapped to",
            InkLineSnapper.Snap([], new NormalizedRegion(0.1, 0.1, 0.2, 0.05), "middle") is null,
            "expected null for a page with no strokes");

        // Strokes of one line arrive as many separate bounds — letters, a superscript sitting
        // slightly higher, an accent. They must group into ONE line, not several.
        List<NormalizedRegion> scattered =
        [
            new(0.10, 0.500, 0.05, 0.030),
            new(0.16, 0.494, 0.05, 0.036), // taller: a tall letter on the same line
            new(0.22, 0.488, 0.03, 0.018), // higher and small: a superscript
            new(0.26, 0.502, 0.05, 0.028),
        ];

        var grouped = InkLineSnapper.Snap(scattered, new NormalizedRegion(0.10, 0.49, 0.22, 0.04), "middle");

        Check(
            "Pieces of one handwritten line group into a single region, superscript included",
            grouped is { } g && g.X < 0.11 && g.X + g.Width > 0.30,
            grouped?.ToString() ?? "null");

        // Two lines close together must not merge — that would highlight the wrong one too.
        List<NormalizedRegion> twoClose =
        [
            new(0.10, 0.40, 0.40, 0.030),
            new(0.10, 0.48, 0.40, 0.030),
        ];

        var lower = InkLineSnapper.Snap(twoClose, new NormalizedRegion(0.10, 0.48, 0.40, 0.030), null);

        Check(
            "Adjacent lines stay separate rather than merging into one tall box",
            lower is { } low && low.Height < 0.06,
            lower?.ToString() ?? "null");
    }

    /// <summary>
    /// The barrel-button lasso decides what a selection contains. Getting this wrong is
    /// invisible until a student circles a line of working and it grabs the wrong half.
    /// </summary>
    private static void TestLassoContainment()
    {
        // A plain square, drawn open — the student's pen never lands exactly back where it
        // started, so the test walks the same path the real one does.
        List<Point> square =
        [
            new(100, 100), new(300, 100), new(300, 300), new(100, 300), new(105, 105),
        ];

        Check(
            "A point inside the lasso is inside",
            PageLassoSelection.Contains(square, new Point(200, 200)),
            "the centre of the square read as outside");

        Check(
            "A point outside the lasso is outside",
            !PageLassoSelection.Contains(square, new Point(400, 200)),
            "a point clear of the square read as inside");

        // A C-shape: the gap between the arms is enclosed by the bounding box but NOT by the
        // outline. A bounding-box test would wrongly claim it, which is the whole reason the
        // crossing test exists.
        List<Point> horseshoe =
        [
            new(0, 0), new(100, 0), new(100, 40), new(40, 40),
            new(40, 60), new(100, 60), new(100, 100), new(0, 100),
        ];

        Check(
            "The hollow of a concave lasso is not selected",
            !PageLassoSelection.Contains(horseshoe, new Point(70, 50)),
            "a point in the mouth of the horseshoe read as inside");

        Check(
            "The solid part of a concave lasso is selected",
            PageLassoSelection.Contains(horseshoe, new Point(20, 50)),
            "a point in the spine of the horseshoe read as outside");

        // Majority rule: a stroke whose tail escapes the lasso still belongs to it.
        var clipped = new Stroke(new StylusPointCollection(new[]
        {
            new StylusPoint(150, 150),
            new StylusPoint(200, 200),
            new StylusPoint(250, 250),
            new StylusPoint(500, 500),
        }));

        Check(
            "A stroke mostly inside the lasso is taken",
            PageLassoSelection.MostlyInside(clipped, square),
            "three of four points were inside and the stroke was still rejected");

        var passing = new Stroke(new StylusPointCollection(new[]
        {
            new StylusPoint(250, 250),
            new StylusPoint(600, 600),
            new StylusPoint(700, 700),
            new StylusPoint(800, 800),
        }));

        Check(
            "A stroke only clipping the lasso is left alone",
            !PageLassoSelection.MostlyInside(passing, square),
            "a stroke with one point inside was swept into the selection");
    }

    private static bool HasRed(byte[] png)
    {
        var decoded = new BitmapImage();
        decoded.BeginInit();
        decoded.CacheOption = BitmapCacheOption.OnLoad;
        decoded.StreamSource = new MemoryStream(png);
        decoded.EndInit();

        var converted = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        for (var i = 0; i < pixels.Length; i += 4)
        {
            // B, G, R, A — strongly red means the stamp layer was painted.
            if (pixels[i + 2] > 150 && pixels[i] < 100 && pixels[i + 1] < 100)
            {
                return true;
            }
        }

        return false;
    }

    private static void TestRenderingAndExport()
    {
        var strokes = BuildStrokes(new Random(7), strokeCount: 12);

        var png = PageRenderer.RenderPagePng(strokes, null, 1000);
        Check("Page rasterizes to a PNG", png.Length > 1000 && png[1] == 'P', $"{png.Length} bytes");

        var crop = PageRenderer.RenderRegionPng(strokes, null, new NormalizedRegion(0.2, 0.2, 0.3, 0.2));
        Check("Region crops are smaller than full pages", crop.Length < png.Length * 2, "crop too large");

        var directory = Path.Combine(Path.GetTempPath(), $"notetaker_export_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var svgPath = Path.Combine(directory, "page.svg");
            ExportService.SaveSvg(svgPath, strokes);
            var svg = File.ReadAllText(svgPath);
            Check(
                "SVG export contains vector paths",
                svg.Contains("<path", StringComparison.Ordinal) && svg.Contains("</svg>", StringComparison.Ordinal),
                "malformed SVG");

            var pngPath = Path.Combine(directory, "page.png");
            ExportService.SavePng(pngPath, strokes, null);
            Check("PNG export writes a file", new FileInfo(pngPath).Length > 1000, "file too small");

            var pdfPath = Path.Combine(directory, "page.pdf");
            ExportService.SavePdf(pdfPath, strokes, null);
            Check("PDF export writes a file", new FileInfo(pdfPath).Length > 1000, "file too small");

            // The real proof: PDFium parses the PDF we just wrote.
            var pageCount = PdfBackgroundService.GetPageCount(pdfPath);
            Check("Exported PDF is readable by PDFium", pageCount == 1, $"{pageCount} pages");

            var background = PdfBackgroundService.Render(pdfPath, 0);
            Check(
                "PDF renders to a page-sized background on this architecture",
                background is not null && Math.Abs(background.Width - PageGeometry.Width) < 2,
                background is null ? "render returned null" : $"{background.Width}x{background.Height}");

            if (background is not null)
            {
                var composed = PageComposer.Compose(background, background, InsertPlacement.Bottom);
                Check(
                    "Composed content keeps page dimensions",
                    Math.Abs(composed.Width - PageGeometry.Width) < 2,
                    $"{composed.Width}");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Temp cleanup is best-effort.
            }
        }

        var model = new ThumbnailEmbeddingModel();
        var dense = model.EmbedImageAsync(png).GetAwaiter().GetResult();
        Check("Page embedding has the expected shape", dense.Length == model.Dimensions, $"{dense.Length} dims");
        Check(
            "Page embedding is normalized",
            Math.Abs(VectorMath.Similarity(dense, dense) - 1d) < 1e-4,
            "not unit length");

        var blank = model.EmbedImageAsync(PageRenderer.RenderPagePng(new StrokeCollection(), null, 1000))
            .GetAwaiter().GetResult();
        Check(
            "A blank page is not similar to a written one",
            VectorMath.Similarity(dense, blank) < 0.5,
            "blank page matched");
    }

    private static async Task TestPracticeModeMakesNoCallsAsync(
        NoteDatabase database,
        InkRepository ink,
        TutorRepository tutorRepo,
        UsageRepository usage)
    {
        var client = new CountingTutorClient();
        using var coordinator = BuildCoordinator(client, ink, tutorRepo, usage, new TestConnectivity(true));

        for (var i = 0; i < 25; i++)
        {
            coordinator.NotifyInkChanged(1, TutorMode.Practice, new NormalizedRegion(0.1, 0.1, 0.2, 0.05));
        }

        await Task.Delay(400);

        Check("Practice mode makes zero API calls", client.ScanCalls == 0, $"{client.ScanCalls} calls");

        var summary = await usage.GetSummaryAsync(DateTimeOffset.UtcNow.AddDays(-1));
        Check("Practice mode logs no usage", summary.Calls == 0, $"{summary.Calls} logged");
        _ = database;
    }

    private static async Task TestLiveModeDebouncesAndCallsAsync(
        NoteDatabase database,
        InkRepository ink,
        TutorRepository tutorRepo,
        UsageRepository usage)
    {
        var client = new CountingTutorClient();
        using var coordinator = BuildCoordinator(client, ink, tutorRepo, usage, new TestConnectivity(true));

        // Twenty strokes in quick succession must collapse into a single check.
        for (var i = 0; i < 20; i++)
        {
            coordinator.NotifyInkChanged(2, TutorMode.Live, new NormalizedRegion(0.1 + (i * 0.01), 0.2, 0.05, 0.03));
            await Task.Delay(5);
        }

        await Task.Delay(500);

        Check("Live mode debounces a burst into one call", client.ScanCalls == 1, $"{client.ScanCalls} calls");

        var feedback = await tutorRepo.GetFeedbackAsync(2);
        Check("Live findings are persisted as highlights", feedback.Count == 1, $"{feedback.Count} rows");

        var summary = await usage.GetSummaryAsync(DateTimeOffset.UtcNow.AddDays(-1));
        Check("Live calls are billed to the usage log", summary.Calls == 1, $"{summary.Calls} logged");
        _ = database;
    }

    private static async Task TestEmptyFindingsPrunedOnEraseAsync(
        NoteDatabase database,
        InkRepository ink,
        TutorRepository tutorRepo,
        UsageRepository usage)
    {
        var client = new CountingTutorClient();
        var snapshots = new StubSnapshotProvider { RegionHasInk = true };
        using var coordinator = BuildCoordinator(client, ink, tutorRepo, usage, new TestConnectivity(true), snapshots);

        coordinator.NotifyInkChanged(21, TutorMode.Live, new NormalizedRegion(0.2, 0.3, 0.2, 0.05));
        await Task.Delay(400);

        var before = await tutorRepo.GetFeedbackAsync(21);
        Check("Prune test starts with a finding", before.Count == 1, $"{before.Count} rows");

        // Simulate the student erasing the ink under that mark — no new vision call needed.
        snapshots.RegionHasInk = false;
        coordinator.NotifyInkChanged(21, TutorMode.Live, new NormalizedRegion(0.2, 0.3, 0.2, 0.05));
        await Task.Delay(200);

        var after = await tutorRepo.GetFeedbackAsync(21);
        Check("Erasing ink dismisses the empty finding", after.Count == 0, $"{after.Count} left");
        _ = database;
    }

    private static async Task TestFixedMistakeClearsInPlaceAsync(
        NoteDatabase database,
        InkRepository ink,
        TutorRepository tutorRepo,
        UsageRepository usage)
    {
        // Same spot, ink never disappears: wrong digit erased and the right one written
        // over it. No new finding ever overlaps the old mark, and the ink hit-test still
        // sees ink there the whole time — the only signal that it was fixed is that the
        // rescan of that exact focus window came back with nothing.
        var client = new CountingTutorClient
        {
            NextFindings = [new(new NormalizedRegion(0.2, 0.3, 0.3, 0.05), FeedbackSeverity.Major, "Sign error")],
        };
        var snapshots = new StubSnapshotProvider { RegionHasInk = true };
        using var coordinator = BuildCoordinator(client, ink, tutorRepo, usage, new TestConnectivity(true), snapshots);

        coordinator.NotifyInkChanged(22, TutorMode.Live, new NormalizedRegion(0.2, 0.3, 0.3, 0.05));
        await Task.Delay(400);

        var before = await tutorRepo.GetFeedbackAsync(22);
        Check("Mistake is flagged before the fix", before.Count == 1, $"{before.Count} rows");

        // Student erased the wrong digit and wrote the correct one in the same spot —
        // ink is still present, and this rescan's focus covers that exact region.
        client.NextFindings = [];
        coordinator.NotifyInkChanged(22, TutorMode.Live, new NormalizedRegion(0.2, 0.3, 0.3, 0.05));
        await Task.Delay(400);

        var after = await tutorRepo.GetFeedbackAsync(22);
        Check("Fixing the mistake in place clears the stale mark", after.Count == 0, $"{after.Count} left");
        _ = database;
    }

    private static async Task TestDistantMistakeSurvivesUnrelatedScanAsync(
        NoteDatabase database,
        InkRepository ink,
        TutorRepository tutorRepo,
        UsageRepository usage)
    {
        // A real mistake near the top of the page gets flagged. The student then writes
        // something unrelated far down the page — the model, correctly following its "only
        // report mistakes intersecting the recent writing" instruction, looks there and
        // reports nothing. The first mistake is still wrong and still has ink under it; the
        // rescan just never looked at it. It must not be cleared by that silence — this is
        // the regression guard for using the padded focusGate instead of the raw focus for
        // that decision (a distant, still-wrong mark would vanish the moment ANY unrelated
        // scan happened nearby-ish, not just when it was itself re-examined and found clean).
        var client = new CountingTutorClient
        {
            NextFindings = [new(new NormalizedRegion(0.2, 0.10, 0.3, 0.06), FeedbackSeverity.Major, "first mistake")],
        };
        var snapshots = new StubSnapshotProvider { RegionHasInk = true };
        using var coordinator = BuildCoordinator(client, ink, tutorRepo, usage, new TestConnectivity(true), snapshots);

        coordinator.NotifyInkChanged(23, TutorMode.Live, new NormalizedRegion(0.2, 0.10, 0.3, 0.06));
        await Task.Delay(400);

        var afterFirst = await tutorRepo.GetFeedbackAsync(23);
        Check("First, distant mistake is flagged", afterFirst.Count == 1, $"{afterFirst.Count} rows");

        client.NextFindings = [];
        coordinator.NotifyInkChanged(23, TutorMode.Live, new NormalizedRegion(0.2, 0.85, 0.3, 0.06));
        await Task.Delay(400);

        var afterSecond = await tutorRepo.GetFeedbackAsync(23);
        Check(
            "Distant, still-wrong mistake survives a scan of an unrelated part of the page",
            afterSecond.Count == 1 && afterSecond[0].Label == "first mistake",
            $"{afterSecond.Count} rows");
        _ = database;
    }

    private static async Task TestChatFollowsFixedMistakeToNextFindingAsync(
        NoteDatabase database,
        InkRepository ink,
        TutorRepository tutorRepo,
        UsageRepository usage)
    {
        // End-to-end reproduction of the exact bug report: three mistakes flagged (top,
        // middle, bottom of the page), the chat is discussing the top one, the student fixes
        // it, and asks for "the next one." It must land on the ORIGINAL middle mistake, not
        // skip past it to the bottom one just because fixing the top one renumbered
        // everything else down by one. Exercises the real dismissal path (TutorCoordinator)
        // feeding the real resolver (ChatAnchorResolver) — the bug was in MainWindow eagerly
        // reassigning the anchor between the two, which neither piece's own tests would catch
        // in isolation.
        var client = new CountingTutorClient();
        var snapshots = new StubSnapshotProvider { RegionHasInk = true };
        using var coordinator = BuildCoordinator(client, ink, tutorRepo, usage, new TestConnectivity(true), snapshots);

        var top = new NormalizedRegion(0.2, 0.08, 0.3, 0.06);
        var middle = new NormalizedRegion(0.2, 0.45, 0.3, 0.06);
        var bottom = new NormalizedRegion(0.2, 0.82, 0.3, 0.06);

        client.NextFindings = [new(top, FeedbackSeverity.Major, "top mistake")];
        coordinator.NotifyInkChanged(24, TutorMode.Live, top);
        await Task.Delay(400);

        client.NextFindings = [new(middle, FeedbackSeverity.Major, "middle mistake")];
        coordinator.NotifyInkChanged(24, TutorMode.Live, middle);
        await Task.Delay(400);

        client.NextFindings = [new(bottom, FeedbackSeverity.Major, "bottom mistake")];
        coordinator.NotifyInkChanged(24, TutorMode.Live, bottom);
        await Task.Delay(400);

        var flagged = await tutorRepo.GetFeedbackAsync(24);
        Check("All three mistakes are flagged", flagged.Count == 3, $"{flagged.Count} rows");

        var topId = flagged.First(f => f.Label == "top mistake").Id;
        var middleId = flagged.First(f => f.Label == "middle mistake").Id;

        // Chat is currently discussing the top mistake (badge #1).
        long? activeAnchorId = topId;

        // Student fixes it in place — same spot, ink stays, the model reports nothing there now.
        client.NextFindings = [];
        coordinator.NotifyInkChanged(24, TutorMode.Live, top);
        await Task.Delay(400);

        var afterFix = await tutorRepo.GetFeedbackAsync(24);
        Check(
            "Fixed mistake is gone, the other two remain",
            afterFix.Count == 2 && afterFix.All(f => f.Label != "top mistake"),
            $"{afterFix.Count} rows");

        // RefreshFeedbackAsync's job when the active anchor vanishes: go to null, and
        // nothing else — never eagerly reassigned to whatever is now first.
        if (afterFix.All(f => f.Id != activeAnchorId))
        {
            activeAnchorId = null;
        }

        // Sidebar/badge order is creation order, same as GetFeedbackAsync already returns.
        var currentIds = afterFix.Select(f => f.Id).ToList();
        var resolved = ChatAnchorResolver.Resolve("ok, let's do the next one", currentIds, activeAnchorId);

        Check(
            "'Next' after fixing the top mistake lands on the original middle one, not the bottom one",
            resolved == middleId,
            $"resolved to {resolved}, expected middle ({middleId})");
        _ = database;
    }

    private static async Task TestOfflineQueueAsync(
        NoteDatabase database,
        InkRepository ink,
        TutorRepository tutorRepo,
        UsageRepository usage)
    {
        var client = new CountingTutorClient();
        var connectivity = new TestConnectivity(online: false);
        using var coordinator = BuildCoordinator(client, ink, tutorRepo, usage, connectivity);

        await coordinator.ReviewPageAsync(3);

        Check("Offline review makes no network call", client.ScanCalls == 0, $"{client.ScanCalls} calls");

        var pending = await tutorRepo.GetPendingJobsAsync(10);
        Check("Offline review is queued", pending.Count == 1, $"{pending.Count} queued");

        connectivity.IsOnline = true;
        var processed = await coordinator.FlushPendingJobsAsync();

        Check("Reconnecting flushes the queue", processed == 1 && client.ScanCalls == 1, $"{processed} processed");

        var stillPending = await tutorRepo.GetPendingJobsAsync(10);
        Check("Flushed jobs leave the queue", stillPending.Count == 0, $"{stillPending.Count} left");
        _ = database;
    }

    private static async Task TestReviewOpensHolisticSessionAsync(
        NoteDatabase database,
        InkRepository ink,
        TutorRepository tutorRepo,
        UsageRepository usage)
    {
        // Review used to hard-delete a page's whole TutorFeedback history on every run
        // (ClearFeedbackAsync), which would destroy the exact signal a weakness-targeting
        // review needs to look at. This exercises the real coordinator end to end: one
        // mistake fixed earlier (dismissed, not deleted) plus one still open, then a fresh
        // scan that repeats the fixed mistake's topic. Expect every row preserved, only the
        // previously-active one soft-dismissed, and a review thread opened — a topic
        // recurring twice is extra context for that opening message now, not a gate on
        // whether it gets sent at all.
        var client = new CountingTutorClient();
        var snapshots = new StubSnapshotProvider { RegionHasInk = true };
        using var coordinator = BuildCoordinator(client, ink, tutorRepo, usage, new TestConnectivity(true), snapshots);

        const long pageId = 61;

        var stillOpen = new TutorFeedback
        {
            PageId = pageId,
            Region = new NormalizedRegion(0.2, 0.1, 0.3, 0.05),
            Severity = FeedbackSeverity.Minor,
            Label = "unit left off the answer",
            Topic = "unit conversion",
            Model = "test",
            OriginMode = TutorMode.Live,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        stillOpen.Id = await tutorRepo.AddFeedbackAsync(stillOpen);

        var fixedEarlier = new TutorFeedback
        {
            PageId = pageId,
            Region = new NormalizedRegion(0.2, 0.3, 0.3, 0.05),
            Severity = FeedbackSeverity.Minor,
            Label = "dropped sign distributing",
            Topic = "sign errors",
            Model = "test",
            OriginMode = TutorMode.Live,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        fixedEarlier.Id = await tutorRepo.AddFeedbackAsync(fixedEarlier);
        await tutorRepo.DismissFeedbackAsync(fixedEarlier.Id);

        client.NextFindings =
        [
            new(new NormalizedRegion(0.2, 0.6, 0.3, 0.05), FeedbackSeverity.Minor, "sign flipped again", "sign errors"),
        ];

        var review = await coordinator.ReviewPageAsync(pageId);

        Check("Review persists the fresh scan's finding", review.Findings.Count == 1, $"{review.Findings.Count}");

        var all = await tutorRepo.GetAllFeedbackForPageAsync(pageId);
        Check("Review never deletes a page's feedback history", all.Count == 3, $"{all.Count} rows");

        var stillOpenAfter = all.First(f => f.Id == stillOpen.Id);
        Check(
            "A previously-active finding is soft-dismissed, not deleted, by a fresh Review",
            stillOpenAfter.Dismissed,
            stillOpenAfter.Dismissed ? "dismissed" : "still active");

        var active = await tutorRepo.GetFeedbackAsync(pageId);
        Check(
            "Only the fresh scan's finding is active afterward",
            active.Count == 1 && active[0].Label == "sign flipped again",
            $"{active.Count} active");

        Check(
            "A recurring topic (fixed-earlier + fresh) opens a holistic review thread",
            review.Thread is not null && review.Opening is not null,
            review.Thread is null ? "no thread opened" : "thread opened");

        Check(
            "Exactly one cheap weakness-review call was made",
            client.WeaknessReviewCalls == 1,
            $"{client.WeaknessReviewCalls}");

        // A different page with no history at all: one fresh finding on a brand new topic.
        // Nothing recurs, but the trigger is no longer "does a topic recur" — it's "did
        // this pass find anything at all" — so this should STILL open a holistic review.
        const long freshPageId = 62;
        client.NextFindings =
        [
            new(new NormalizedRegion(0.2, 0.2, 0.3, 0.05), FeedbackSeverity.Minor, "one-off slip", "arithmetic"),
        ];

        var reviewNoHistory = await coordinator.ReviewPageAsync(freshPageId);

        Check(
            "A single, non-recurring mistake still opens a holistic review now",
            reviewNoHistory.Thread is not null && reviewNoHistory.Opening is not null,
            reviewNoHistory.Thread is null ? "no thread opened" : "thread opened");

        Check(
            "A second review (different page) spends a second weakness-review call",
            client.WeaknessReviewCalls == 2,
            $"{client.WeaknessReviewCalls}");

        // A genuinely clean page — the one case that should still open nothing and spend
        // nothing, since there's no mistake at all to holistically review.
        const long cleanPageId = 63;
        client.NextFindings = [];

        var reviewClean = await coordinator.ReviewPageAsync(cleanPageId);

        Check(
            "A clean page with nothing flagged opens no thread",
            reviewClean.Thread is null && reviewClean.Opening is null,
            reviewClean.Thread is null ? "none opened" : "thread opened");

        Check(
            "No weakness-review call is spent on a clean page",
            client.WeaknessReviewCalls == 2,
            $"{client.WeaknessReviewCalls}");

        _ = database;
    }

    /// <summary>
    /// A vision model answers in coordinates relative to the picture it was shown. Once the
    /// capture follows the student onto open canvas that picture is no longer the sheet, so
    /// the answer has to be mapped back before it means anything. Without that step every
    /// highlight lands on the sheet no matter where the mistake actually is — which is worse
    /// than not flagging at all, because it points confidently at the wrong place.
    /// </summary>
    private static async Task TestFindingsMapBackFromOffSheetCaptureAsync(
        NoteDatabase database,
        InkRepository ink,
        TutorRepository tutorRepo,
        UsageRepository usage)
    {
        var client = new CountingTutorClient();

        // The capture covered a half-height window one whole sheet BELOW the sheet itself.
        var snapshots = new StubSnapshotProvider
        {
            RegionHasInk = true,
            Area = new NormalizedRegion(0, 1.0, 1.0, 0.5),
        };

        using var coordinator = BuildCoordinator(client, ink, tutorRepo, usage, new TestConnectivity(true), snapshots);

        // Dead centre of that image.
        client.NextFindings =
        [
            new(new NormalizedRegion(0.4, 0.4, 0.2, 0.2), FeedbackSeverity.Major, "off-sheet slip", "algebra"),
        ];

        const long pageId = 71;
        await coordinator.ReviewPageAsync(pageId);

        var saved = await tutorRepo.GetFeedbackAsync(pageId);
        Check("A finding from an off-sheet capture is stored", saved.Count == 1, $"{saved.Count}");

        if (saved.Count != 1)
        {
            return;
        }

        // y = 1.0 + 0.4 * 0.5 = 1.2 — below the sheet, which is the whole point. Clamping
        // anywhere along the way would pin this to 1.0 and put the mark on the bottom edge.
        var region = saved[0].Region;
        Check(
            "Its position is mapped out of image space and back onto the page",
            Math.Abs(region.X - 0.4) < 0.001 && Math.Abs(region.Y - 1.2) < 0.001,
            $"x={region.X:0.###} y={region.Y:0.###}, expected x=0.4 y=1.2");

        Check(
            "Its size is scaled by the captured area, not left in image units",
            Math.Abs(region.Width - 0.2) < 0.001 && Math.Abs(region.Height - 0.1) < 0.001,
            $"w={region.Width:0.###} h={region.Height:0.###}, expected w=0.2 h=0.1");

        _ = database;
    }

    private static async Task TestBudgetCapsAsync(NoteDatabase database, UsageRepository usage)
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var options = new TutorOptions
        {
            MaxLiveCallsPerHour = 3,
            MonthlyCostCapUsd = 30000m,
        };

        var budget = new TutorBudget(usage, clock, options);

        for (var i = 0; i < 3; i++)
        {
            var allowed = await budget.CheckAsync(TutorCallType.LiveCheck);
            if (!allowed.Allowed)
            {
                Check("Hourly cap allows calls under the limit", false, allowed.Reason ?? "blocked early");
                return;
            }

            budget.RecordLiveCall();
        }

        var blocked = await budget.CheckAsync(TutorCallType.LiveCheck);
        Check("Hourly cap blocks the call over the limit", !blocked.Allowed, "not blocked");

        clock.Advance(TimeSpan.FromMinutes(61));
        var afterHour = await budget.CheckAsync(TutorCallType.LiveCheck);
        Check("Hourly cap resets after an hour", afterHour.Allowed, afterHour.Reason ?? "still blocked");

        // Now prove the money cap bites, independent of call counts.
        await usage.LogAsync(new ApiUsageLog
        {
            CallType = TutorCallType.ReviewScan,
            Model = "test-vision",
            TokensIn = 1,
            TokensOut = 1,
            CostEstimate = 5m,
            CreatedAt = clock.UtcNow,
        });

        var tightBudget = new TutorBudget(
            usage,
            clock,
            new TutorOptions { MaxLiveCallsPerHour = 100, MonthlyCostCapUsd = 1m });

        var overspent = await tightBudget.CheckAsync(TutorCallType.ReviewScan);
        Check("Daily budget stops spending", !overspent.Allowed, "not blocked");
        Check(
            "Budget refusal explains itself",
            !string.IsNullOrWhiteSpace(overspent.Reason),
            "no reason given");
        _ = database;
    }

    private static void TestScanParser()
    {
        var fenced = """
            Sure, here is what I found:
            ```json
            { "regions": [ { "x": 0.1, "y": 0.2, "w": 0.3, "h": 0.05,
              "severity": "major", "label": "Dropped a negative" } ],
              "summary": "Check line two." }
            ```
            """;

        var (findings, summary) = ScanResponseParser.Parse(fenced);
        Check("Parser recovers JSON from a fenced reply", findings.Count == 1, $"{findings.Count} findings");
        Check("Parser reads the summary", summary == "Check line two.", summary);
        Check(
            "Parser maps severity",
            findings.Count == 1 && findings[0].Severity == FeedbackSeverity.Major,
            "wrong severity");

        var alternateKeys = """{"regions":[{"x":0.5,"y":0.5,"width":0.2,"height":0.04,"short_label":"Units"}]}""";
        var (alt, _) = ScanResponseParser.Parse(alternateKeys);
        Check("Parser accepts width/height and short_label", alt.Count == 1, $"{alt.Count} findings");

        var zeroArea = """{"regions":[{"x":0.2,"y":0.2,"w":0,"h":0,"label":"Here"}]}""";
        var (zero, _) = ScanResponseParser.Parse(zeroArea);
        Check(
            "Zero-area regions become drawable underlines",
            zero.Count == 1 && !zero[0].Region.IsEmpty,
            "region still empty");

        var (junk, _) = ScanResponseParser.Parse("I could not read the page.");
        Check("Non-JSON replies degrade to no findings", junk.Count == 0, $"{junk.Count} findings");

        var (empty, _) = ScanResponseParser.Parse("""{"regions":[],"summary":"Looks right."}""");
        Check("A clean page yields no highlights", empty.Count == 0, $"{empty.Count} findings");

        // gemini-3.1-flash-lite occasionally answers on its native 0..1000 box scale instead
        // of the 0..1 fraction the prompt asks for. Before the fix this clamped straight to
        // {1,1,0,0} — a dead zero-area box nowhere near the ink (see debug-e84c78.log).
        var thousandScale = """{"regions":[{"x":180,"y":380,"w":160,"h":50,"label":"sign flipped here"}]}""";
        var (rescaled, _) = ScanResponseParser.Parse(thousandScale);
        Check(
            "0..1000-scale coordinates are rescaled, not clamped into a corner",
            rescaled.Count == 1
                && Math.Abs(rescaled[0].Region.X - 0.18) < 0.001
                && Math.Abs(rescaled[0].Region.Y - 0.38) < 0.001
                && Math.Abs(rescaled[0].Region.Width - 0.16) < 0.001
                && Math.Abs(rescaled[0].Region.Height - 0.05) < 0.001,
            rescaled.Count == 1 ? rescaled[0].Region.ToString() : $"{rescaled.Count} findings");

        var mixedScaleBatch = """
            {"regions":[
              {"x":180,"y":380,"w":160,"h":50,"label":"first"},
              {"x":0.6,"y":0.6,"w":0.1,"h":0.05,"label":"second"}
            ]}
            """;
        var (mixed, _) = ScanResponseParser.Parse(mixedScaleBatch);
        Check(
            "A mixed batch keeps the already-correct fraction box untouched",
            mixed.Count == 2
                && Math.Abs(mixed[0].Region.X - 0.18) < 0.001
                && Math.Abs(mixed[1].Region.X - 0.6) < 0.001,
            $"{mixed.Count} findings");
    }

    private static void TestChatAnchorResolver()
    {
        // Four findings; discussing #2 (stable id 11).
        var before = new List<long> { 10, 11, 12, 13 };

        Check(
            "Explicit number resolves against the current numbering",
            ChatAnchorResolver.Resolve("what about error 3", before, currentAnchorId: 11) == 12,
            "wrong id");

        Check(
            "Ordinal word resolves the same way as a digit",
            ChatAnchorResolver.Resolve("let's look at the third one", before, currentAnchorId: 11) == 12,
            "wrong id");

        Check(
            "Out-of-range number falls back to the current anchor rather than guessing",
            ChatAnchorResolver.Resolve("what about error 9", before, currentAnchorId: 11) == 11,
            "should have kept the anchor");

        Check(
            "'Next' steps to whoever immediately follows the current anchor",
            ChatAnchorResolver.Resolve("let's do the next one", before, currentAnchorId: 11) == 12,
            "wrong id");

        // Error 1 (id 10) gets fixed mid-conversation and drops off the list — everything
        // after it renumbers down by one. The anchor (id 11) is unaffected by identity, but
        // its DISPLAYED number just silently changed from 2 to 1. A resolver that stepped
        // "next" by adding 1 to a remembered *number* would now land on id 13 (the old
        // "number 3") and skip id 12 entirely — the exact bug this class exists to avoid.
        var afterFix = new List<long> { 11, 12, 13 };
        Check(
            "'Next' after a renumbering steps to the right neighbour, not a skipped one",
            ChatAnchorResolver.Resolve("ok, next one", afterFix, currentAnchorId: 11) == 12,
            "skipped a finding because of the renumbering");

        // The anchor itself was the one fixed and removed — nothing valid to step "next"
        // from, so land on whatever is now first rather than guessing an offset.
        var afterAnchorFixed = new List<long> { 12, 13 };
        Check(
            "'Next' with a vanished anchor lands on the first still-open finding",
            ChatAnchorResolver.Resolve("next", afterAnchorFixed, currentAnchorId: 11) == 12,
            "wrong fallback");

        Check(
            "A plain follow-up with no number or 'next' keeps the current anchor",
            ChatAnchorResolver.Resolve("why is this wrong?", before, currentAnchorId: 12) == 12,
            "anchor moved without being asked");

        Check(
            "No anchor yet defaults to the first finding",
            ChatAnchorResolver.Resolve("what's wrong with this page?", before, currentAnchorId: null) == 10,
            "wrong default");

        Check(
            "Nothing flagged at all resolves to no anchor",
            ChatAnchorResolver.Resolve("what's wrong with this page?", [], currentAnchorId: null) is null,
            "should be null");
    }

    private static async Task TestSearchAsync(
        PageRepository pages,
        EmbeddingRepository embeddings,
        SearchService search,
        long sectionId)
    {
        var derivation = await pages.CreatePageAsync(sectionId, "Chain rule derivation");
        var similar = await pages.CreatePageAsync(sectionId, "Chain rule practice");
        var unrelated = await pages.CreatePageAsync(sectionId, "Reading list");

        await embeddings.UpsertAsync(new PageEmbedding
        {
            PageId = derivation.Id,
            Vector = VectorMath.Normalize([1f, 0.2f, 0f, 0f]),
            RecognizedText = "chain rule derivative composite function",
        });

        await embeddings.UpsertAsync(new PageEmbedding
        {
            PageId = similar.Id,
            Vector = VectorMath.Normalize([0.95f, 0.3f, 0f, 0f]),
            RecognizedText = "practice problems",
        });

        await embeddings.UpsertAsync(new PageEmbedding
        {
            PageId = unrelated.Id,
            Vector = VectorMath.Normalize([0f, 0f, 1f, 0.1f]),
            RecognizedText = "books to read this term",
        });

        var keyword = await search.SearchKeywordAsync("derivative");
        Check(
            "Keyword search finds recognized ink",
            keyword.Any(r => r.PageId == derivation.Id),
            $"{keyword.Count} results");

        var byTitle = await search.SearchKeywordAsync("Reading list");
        Check("Keyword search finds page titles", byTitle.Any(r => r.PageId == unrelated.Id), "title miss");

        var visual = await search.FindSimilarPagesAsync(derivation.Id);
        Check(
            "Visual search ranks the similar page first",
            visual.Count > 0 && visual[0].PageId == similar.Id,
            visual.Count == 0 ? "no results" : $"top was {visual[0].PageId}");

        Check(
            "Visual search excludes the source page",
            visual.All(r => r.PageId != derivation.Id),
            "source page returned");

        var related = await search.ComputeRelatedAsync(derivation.Id);
        await embeddings.ReplaceRelatedAsync(derivation.Id, related);
        var stored = await embeddings.GetRelatedAsync(derivation.Id);
        Check("Related pages persist", stored.Count > 0, "nothing stored");

        // Vectors are stored normalized, which is what makes the dot product a cosine
        // and therefore independent of how much ink is on the page.
        Check(
            "Normalized similarity ignores magnitude",
            Math.Abs(VectorMath.Similarity(
                VectorMath.Normalize([1f, 0f]),
                VectorMath.Normalize([5f, 0f])) - 1d) < 1e-5,
            "not scale invariant");

        var roundTripped = VectorMath.FromBytes(VectorMath.ToBytes([0.25f, -0.5f, 0.75f]));
        Check(
            "Embeddings survive the blob round trip",
            roundTripped.Length == 3 && Math.Abs(roundTripped[1] + 0.5f) < 1e-6,
            "vector corrupted");
    }

    private static void TestExpressionEvaluator()
    {
        Check("Operator precedence", Near(Eval("2+3*4", 0), 14), "2+3*4");
        Check("Right-associative powers", Near(Eval("2^3^2", 0), 512), "2^3^2");
        Check("Unary minus binds looser than powers", Near(Eval("-x^2", 3), -9), "-x^2 at 3");
        Check("Signed exponents", Near(Eval("2^-2", 0), 0.25), "2^-2");
        Check("Functions and constants", Near(Eval("sin(pi/2)", 0), 1), "sin(pi/2)");
        Check("Variable substitution", Near(Eval("x^2-3*x+2", 5), 12), "x^2-3x+2 at 5");

        var rejected = false;
        try
        {
            _ = ExpressionEvaluator.Compile("2+*");
        }
        catch (FormatException)
        {
            rejected = true;
        }

        Check("Malformed expressions are rejected", rejected, "no error raised");
    }

    private static void TestFindingDeduper()
    {
        var a = new TutorRegionFinding(new NormalizedRegion(0.1, 0.2, 0.2, 0.05), FeedbackSeverity.Minor, "sign");
        var b = new TutorRegionFinding(new NormalizedRegion(0.12, 0.21, 0.18, 0.05), FeedbackSeverity.Major, "sign again");
        var c = new TutorRegionFinding(new NormalizedRegion(0.6, 0.6, 0.2, 0.05), FeedbackSeverity.Minor, "elsewhere");

        var deduped = FindingDeduper.Dedupe([a, b, c]);
        Check("Overlapping findings collapse to one", deduped.Count == 2, $"{deduped.Count}");
        Check("Higher severity wins overlap", deduped.Any(f => f.Severity == FeedbackSeverity.Major), "major dropped");
        Check("Distant finding kept", deduped.Any(f => f.Label == "elsewhere"), "distant dropped");

        var outer = new TutorRegionFinding(new NormalizedRegion(0.1, 0.1, 0.4, 0.2), FeedbackSeverity.Minor, "outer");
        var inner = new TutorRegionFinding(new NormalizedRegion(0.15, 0.12, 0.1, 0.08), FeedbackSeverity.Major, "inner");
        var nested = FindingDeduper.Dedupe([outer, inner]);
        Check("Nested boxes collapse via IoMin", nested.Count == 1, $"{nested.Count}");
        Check("Nested keep prefers major", nested[0].Severity == FeedbackSeverity.Major, nested[0].Label);

        // Two boxes side by side, distinct enough to both survive Dedupe on their raw
        // (pre-acceptance) coordinates — mirrors what AcceptFindingRegionAsync sees before
        // it nudges anything onto ink.
        var left = new TutorRegionFinding(new NormalizedRegion(0.30, 0.40, 0.15, 0.05), FeedbackSeverity.Major, "left");
        var right = new TutorRegionFinding(new NormalizedRegion(0.40, 0.40, 0.15, 0.05), FeedbackSeverity.Major, "right");
        var beforeExpand = FindingDeduper.Dedupe([left, right]);
        Check("Adjacent boxes stay distinct before expansion", beforeExpand.Count == 2, $"{beforeExpand.Count}");

        // AcceptFindingRegionAsync's expand-once step (Inflate(0.04)) runs on each survivor
        // independently. Applying that same inflate here and re-running Dedupe is exactly the
        // fix in TutorCoordinator: it should collapse what the first pass correctly kept apart.
        var expanded = beforeExpand.Select(f => f with { Region = f.Region.Inflate(0.04) }).ToList();
        var afterExpand = FindingDeduper.Dedupe(expanded);
        Check(
            "Expansion-induced overlap is caught by a second Dedupe pass",
            afterExpand.Count == 1,
            $"{afterExpand.Count}");

        // Reported bug: one real mistake ("6+47=180000") came back as two findings with the
        // *identical* label, one box over the operands and one over the result — genuinely
        // non-overlapping, so geometric IoU/IoMin alone can never merge them.
        var operands = new TutorRegionFinding(
            new NormalizedRegion(0.10, 0.40, 0.15, 0.05), FeedbackSeverity.Major, "Incorrect arithmetic addition result");
        var result = new TutorRegionFinding(
            new NormalizedRegion(0.30, 0.41, 0.15, 0.05), FeedbackSeverity.Major, "Incorrect arithmetic addition result");
        var sameLineMerged = FindingDeduper.Dedupe([operands, result]);
        Check(
            "Same-line, same-label boxes merge even when they don't overlap",
            sameLineMerged.Count == 1
                && Math.Abs(sameLineMerged[0].Region.X - 0.10) < 0.001
                && Math.Abs(sameLineMerged[0].Region.Width - 0.35) < 0.001,
            sameLineMerged.Count == 1 ? sameLineMerged[0].Region.ToString() : $"{sameLineMerged.Count} findings");

        // Same label, but a genuinely different line of work — must NOT merge just because
        // the wording happens to match (a recurring habit, not a duplicate report).
        var differentLine = new TutorRegionFinding(
            new NormalizedRegion(0.10, 0.70, 0.15, 0.05), FeedbackSeverity.Major, "Incorrect arithmetic addition result");
        var stillSeparate = FindingDeduper.Dedupe([operands, differentLine]);
        Check(
            "Same label on a different line stays separate",
            stillSeparate.Count == 2,
            $"{stillSeparate.Count}");
    }

    private static void TestWeaknessAggregator()
    {
        TutorFeedback Make(string? topic, FeedbackSeverity severity, string label, DateTimeOffset createdAt) => new()
        {
            PageId = 1,
            Region = new NormalizedRegion(0.1, 0.1, 0.1, 0.1),
            Severity = severity,
            Label = label,
            Topic = topic,
            Model = "test",
            CreatedAt = createdAt,
        };

        var now = DateTimeOffset.UtcNow;

        var oneOff = Make("arithmetic", FeedbackSeverity.Minor, "one slip", now);
        var noTopic = Make(null, FeedbackSeverity.Minor, "untagged", now);
        var signA = Make("sign errors", FeedbackSeverity.Minor, "dropped sign step 1", now.AddMinutes(1));
        var signB = Make("sign errors", FeedbackSeverity.Major, "dropped sign step 2 (worse)", now.AddMinutes(2));
        var unitsA = Make("unit conversion", FeedbackSeverity.Minor, "left off units", now.AddMinutes(3));
        var unitsB = Make("unit conversion", FeedbackSeverity.Minor, "wrong prefix", now.AddMinutes(4));
        var unitsC = Make("unit conversion", FeedbackSeverity.Minor, "wrong prefix again", now.AddMinutes(5));

        var ranked = WeaknessAggregator.Rank([oneOff, noTopic, signA, signB, unitsA, unitsB, unitsC]);

        Check(
            "A single occurrence never counts as a weakness",
            ranked.All(t => t.Topic != "arithmetic"),
            "arithmetic leaked through");
        Check(
            "Findings with no topic are ignored",
            ranked.All(t => !string.IsNullOrEmpty(t.Topic)),
            "an empty/null topic leaked through");
        Check(
            "Recurring topics are found",
            ranked.Any(t => t.Topic == "sign errors") && ranked.Any(t => t.Topic == "unit conversion"),
            $"{ranked.Count} topics");

        var unitsRanked = ranked.First(t => t.Topic == "unit conversion");
        Check("Count reflects every occurrence of the topic", unitsRanked.Count == 3, $"{unitsRanked.Count}");
        Check("Most-recurring topic ranks first", ranked[0].Topic == "unit conversion", ranked[0].Topic);

        var signRanked = ranked.First(t => t.Topic == "sign errors");
        Check(
            "Max severity across occurrences is kept",
            signRanked.MaxSeverity == FeedbackSeverity.Major,
            signRanked.MaxSeverity.ToString());
        Check(
            "Sample label comes from the most recent occurrence",
            signRanked.SampleLabel == "dropped sign step 2 (worse)",
            signRanked.SampleLabel);

        var top1 = WeaknessAggregator.Rank([signA, signB, unitsA, unitsB, unitsC], top: 1);
        Check("`top` caps how many topics are returned", top1.Count == 1, $"{top1.Count}");

        Check("Empty history returns nothing", WeaknessAggregator.Rank([]).Count == 0, "non-empty");
    }

    private static async Task TestOpenAiTransportCacheFieldsAsync()
    {
        // Real bug report: switching Chat to Gemini's OpenAI-compatible endpoint 400'd with
        // "Unknown name \"prompt_cache_key\": Cannot find field." — the shim speaks the same
        // /chat/completions shape but doesn't implement OpenAI's explicit-cache extension, and
        // validates strictly enough to reject it outright rather than ignore it.
        const string canned =
            """{"choices":[{"message":{"content":"ok"}}],"usage":{"prompt_tokens":10,"completion_tokens":5}}""";

        var request = new LlmRequest(
            "some-model",
            "system prompt",
            [new LlmMessage("user", "hi")],
            100,
            PromptCacheKey: "notetaker-thread-1",
            ExplicitPromptCache: true,
            CacheSystemPrompt: true);

        var geminiHandler = new RecordingHttpMessageHandler(canned);
        var geminiTransport = new OpenAiTransport(
            new HttpClient(geminiHandler), "https://example.test", () => "key", "Gemini", supportsExplicitCache: false);
        await geminiTransport.SendAsync(request);

        Check(
            "A transport without explicit-cache support never sends OpenAI's cache fields",
            geminiHandler.LastRequestBody is not null
                && !geminiHandler.LastRequestBody.Contains("prompt_cache_key")
                && !geminiHandler.LastRequestBody.Contains("prompt_cache_options")
                && !geminiHandler.LastRequestBody.Contains("prompt_cache_breakpoint"),
            geminiHandler.LastRequestBody ?? "null");

        var openAiHandler = new RecordingHttpMessageHandler(canned);
        var openAiTransport = new OpenAiTransport(
            new HttpClient(openAiHandler), "https://example.test", () => "key", "OpenAiCompatible", supportsExplicitCache: true);
        await openAiTransport.SendAsync(request);

        Check(
            "A transport with explicit-cache support still sends them",
            openAiHandler.LastRequestBody is not null
                && openAiHandler.LastRequestBody.Contains("prompt_cache_key")
                && openAiHandler.LastRequestBody.Contains("prompt_cache_options"),
            openAiHandler.LastRequestBody ?? "null");
    }

    /// <summary>
    /// Gemini 3.x charges hidden reasoning tokens against <c>max_tokens</c> while excluding
    /// them from the reported <c>completion_tokens</c>, so an un-hinted call silently returns
    /// a truncated reply and a usage row that looks perfectly healthy. Measured against the
    /// live endpoint: same prompt, cap 100 → 4 visible tokens, 500 → 14, 1200 → all 115. That
    /// invisibility is exactly why this needs a test rather than a comment.
    /// </summary>
    private static async Task TestReasoningEffortWiringAsync()
    {
        const string canned =
            """{"choices":[{"message":{"content":"ok"}}],"usage":{"prompt_tokens":10,"completion_tokens":5}}""";

        var request = new LlmRequest(
            "some-model", "system prompt", [new LlmMessage("user", "hi")], 500);

        async Task<string> BodyForAsync(string? effort)
        {
            var handler = new RecordingHttpMessageHandler(canned);
            var transport = new OpenAiTransport(
                new HttpClient(handler),
                "https://example.test",
                () => "key",
                "Gemini",
                supportsExplicitCache: false,
                reasoningEffort: effort);
            await transport.SendAsync(request);
            return handler.LastRequestBody ?? string.Empty;
        }

        var withEffort = await BodyForAsync("low");
        Check(
            "A Gemini transport sends reasoning_effort so thinking cannot eat the reply budget",
            withEffort.Contains("\"reasoning_effort\":\"low\""),
            withEffort);

        // Thinking tokens are billed as output but omitted from completion_tokens. If they are
        // not recovered from total_tokens the daily cap counts a fraction of real spend.
        var thinking = new RecordingHttpMessageHandler(
            """{"choices":[{"message":{"content":"ok"}}],"usage":{"prompt_tokens":73,"completion_tokens":58,"total_tokens":832}}""");
        var thinkingTransport = new OpenAiTransport(
            new HttpClient(thinking), "https://example.test", () => "key", "Gemini",
            supportsExplicitCache: false, reasoningEffort: "low");
        var thought = await thinkingTransport.SendAsync(request);

        Check(
            "Hidden thinking tokens are billed, not lost to the reported completion count",
            thought.Usage.TokensOut == 832 - 73,
            $"counted {thought.Usage.TokensOut}, should be {832 - 73}");

        // gemini-3.1-pro-preview 400s on "minimal", and a 400 takes the whole tutor down
        // rather than degrading it — so "send nothing" has to stay reachable.
        var withoutEffort = await BodyForAsync(null);
        Check(
            "A transport given no reasoning effort omits the field entirely",
            !withoutEffort.Contains("reasoning_effort"),
            withoutEffort);

        Check(
            "Chat's output ceiling leaves room for a thinking model",
            new LlmSettings().MaxOutputTokensFor(TutorCallType.SocraticChat) >= 1200,
            new LlmSettings().MaxOutputTokensFor(TutorCallType.SocraticChat).ToString());
    }

    /// <summary>
    /// Importing a syllabus is the one setup step, so it has to survive a real table of
    /// contents pasted straight out of a textbook: dot leaders, trailing page numbers, chapter
    /// headers between the lessons, and blank lines. It also has to cost nothing — a course is
    /// set up once, and paying a vision call to read text the student could paste would be an
    /// odd place to start spending.
    /// </summary>
    private static void TestSyllabusParsing()
    {
        var pasted = string.Join(
            "\n",
            "Chapter 5  Multiple Integration",
            "",
            "5.1 Double Integrals over Rectangles .......... 312",
            "5.2  Iterated Integrals   313",
            "5.6 Center of Mass",
            "",
            "Chapter 14 Vector Calculus",
            "14.1 Vector Fields ... 501",
            "14.10 Stokes' Theorem");

        var entries = SyllabusParser.Parse(pasted);

        Check(
            "Every numbered lesson is found, and nothing else is",
            entries.Count == 5,
            $"{entries.Count} entries: {string.Join(" / ", entries.Select(e => e.DisplayName))}");

        Check(
            "Dot leaders and trailing page numbers are stripped from the title",
            entries[0].Title == "Double Integrals over Rectangles",
            $"[{entries[0].Title}]");

        Check(
            "A bare trailing page number is stripped too",
            entries[1].Title == "Iterated Integrals",
            $"[{entries[1].Title}]");

        Check(
            "The unit comes from the lesson number, so chapter headings are not needed",
            entries[0].Unit == 5 && entries[4].Unit == 14,
            $"{entries[0].Unit} and {entries[4].Unit}");

        Check(
            "A two-digit lesson number is not mistaken for a different unit",
            entries[4].Number == "14.10" && entries[4].Title == "Stokes' Theorem",
            $"{entries[4].Number} [{entries[4].Title}]");

        Check(
            "A lesson is named the way the textbook names it",
            entries[2].DisplayName == "5.6 Center of Mass",
            entries[2].DisplayName);

        // A syllabus with no numbering at all — an SAT course, say — still imports.
        var unnumbered = SyllabusParser.Parse("Reading\nWriting and Language\nMath: No Calculator\n");
        Check(
            "An unnumbered syllabus imports as a flat list of lessons",
            unnumbered.Count == 3 && unnumbered.All(e => e.Unit is null),
            $"{unnumbered.Count} entries");

        // But once numbering exists, prose between the lessons is noise and stays out.
        var noisy = SyllabusParser.Parse(
            "This course covers multivariable calculus and is graded on a curve.\n5.1 Double Integrals\n");
        Check(
            "Prose alongside numbered lessons is left out rather than imported as a lesson",
            noisy.Count == 1 && noisy[0].Number == "5.1",
            $"{noisy.Count} entries: {string.Join(" / ", noisy.Select(e => e.DisplayName))}");

        Check(
            "The same lesson pasted twice is imported once",
            SyllabusParser.Parse("5.1 Double Integrals\n5.1 Double Integrals\n").Count == 1,
            "a duplicate line created a second lesson");

        Check(
            "Nothing at all parses to nothing, rather than throwing",
            SyllabusParser.Parse("   ").Count == 0,
            "whitespace produced entries");
    }

    /// <summary>
    /// The shape of a real course syllabus, taken from the Westcott Calculus III PDF the student
    /// supplied: pages of prose, a running page header, chapter headings between the lessons,
    /// each lesson number repeated bare on the two lines after its title, and — the one that
    /// actually broke the parser — a week-by-week schedule near the end whose rows are section
    /// RANGES. "5.1 - 5.2" matches the lesson pattern exactly and imported as a lesson called
    /// "- 5.2", sixteen times over.
    /// </summary>
    private static void TestRealSyllabusShape()
    {
        var syllabus = string.Join(
            "\n",
            "Course Information",
            "Westcott Courses",
            "Presents a study of differentiation and integration of functions of several variables.",
            "At the conclusion of this course, students should be able to:",
            "1.",
            "Understand vector algebra and elementary differential vector calculus.",
            "Content Menu",
            "Chapter 5 - Multiple Integration",
            // The content menu is a table: the progress columns beside the title repeat
            // the number on the SAME line, which is how a PDF reader hands it over.
            "5.1 Double Integrals over Rectangular Regions 5.1 5.1",
            "5.2 Double Integrals over General Regions",
            "5.2",
            "5.2",
            "Chapter 5 Test",
            "Chapter 6 - Vector Calculus",
            "6.4 Green's Theorem",
            "6.4",
            "6.4",
            "Week",
            "Complete Sections",
            "12",
            "5.1 - 5.2",
            "13",
            "5.3 - 5.5");

        var lessons = SyllabusParser.Parse(syllabus);

        Check(
            "Only the real lessons are imported from a whole syllabus",
            lessons.Count == 3,
            $"{lessons.Count}: {string.Join(" / ", lessons.Select(e => e.DisplayName))}");

        Check(
            "A week-by-week range is not a lesson",
            lessons.All(e => !e.Title.StartsWith('-')),
            string.Join(" / ", lessons.Select(e => e.DisplayName)));

        Check(
            "A lesson number repeated bare after its title does not duplicate it",
            lessons.Count(e => e.Number == "5.1") == 1,
            "the bare repeats created extra lessons");

        Check(
            "Prose, headings and the numbered learning outcomes stay out",
            lessons.Select(e => e.DisplayName).SequenceEqual(
            [
                "5.1 Double Integrals over Rectangular Regions",
                "5.2 Double Integrals over General Regions",
                "6.4 Green's Theorem",
            ]),
            string.Join(" / ", lessons.Select(e => e.DisplayName)));

        Check(
            "Chapters carry through as units",
            lessons[0].Unit == 5 && lessons[2].Unit == 6,
            $"{lessons[0].Unit} and {lessons[2].Unit}");
    }

    /// <summary>
    /// Lessons fall into textbook order on their own. Sorting by the order they were created
    /// drops a lesson added afterwards at the end of its unit — add 5.6 to an imported syllabus
    /// and it lands under 5.7 — and sorting by name is worse, because "5.10" precedes "5.9" when
    /// the comparison is textual.
    /// </summary>
    private static void TestLessonsSortByTheirNumber()
    {
        var added = new[]
        {
            "5.1 Double Integrals over Rectangular Regions",
            "5.7 Change of Variables in Multiple Integrals",
            "5.10 A Later Lesson",
            "5.6 Calculating Centers of Mass",
            "5.9 Another Lesson",
        };

        var sorted = added
            .OrderBy(n => SyllabusParser.NumberOf(n)?.Lesson ?? int.MaxValue)
            .Select(n => SyllabusParser.NumberOf(n)!.Value.Lesson)
            .ToArray();

        Check(
            "A lesson added later sits at its number, not at the end",
            sorted.SequenceEqual([1, 6, 7, 9, 10]),
            string.Join(", ", sorted));

        Check(
            "Lesson 10 sorts after lesson 9 rather than after lesson 1",
            SyllabusParser.NumberOf("5.10 A Later Lesson")!.Value.Lesson == 10,
            "the number was read as text");

        Check(
            "A lesson with no number is still placed, at the end",
            SyllabusParser.NumberOf("Chapter 5 Test") is null,
            "an unnumbered lesson claimed a number");
    }

    /// <summary>
    /// The SSE parser is the part of streaming most likely to hide a subtle bug: frames arrive
    /// split across socket reads, and a parser that treats each read as a message drops tokens
    /// silently — the reply still looks plausible, just wrong.
    /// </summary>
    private static async Task TestStreamingTransportAsync()
    {
        // Deliberately awkward: a frame carrying multi-byte UTF-8, an empty-content frame, an
        // SSE comment line, and the trailing usage-only frame OpenAI sends with
        // stream_options.include_usage.
        const string sse =
            """
            : keep-alive

            data: {"choices":[{"delta":{"content":"The "},"finish_reason":null}]}

            data: {"choices":[{"delta":{"content":"integral "},"finish_reason":null}]}

            data: {"choices":[{"delta":{},"finish_reason":null}]}

            data: {"choices":[{"delta":{"content":"of x² is "},"finish_reason":null}]}

            data: {"choices":[{"delta":{"content":"$\\frac{x^3}{3}$"},"finish_reason":"stop"}]}

            data: {"choices":[],"usage":{"prompt_tokens":120,"completion_tokens":18,"prompt_tokens_details":{"cached_tokens":80}}}

            data: [DONE]

            """;

        var request = new LlmRequest("m", "sys", [new LlmMessage("user", "hi")], 500);

        // 7 bytes per read guarantees every frame spans several reads.
        var handler = new DribblingSseHandler(sse, bytesPerRead: 7);
        var transport = new OpenAiTransport(
            new HttpClient(handler), "https://example.test", () => "key", "Gemini", supportsExplicitCache: false);

        var deltas = new List<string>();
        var result = await transport.StreamAsync(request, d => deltas.Add(d));

        Check(
            "A reply split across read boundaries reassembles exactly",
            result.Content == "The integral of x² is $\\frac{x^3}{3}$",
            $"got: {result.Content}");

        Check(
            "It actually streamed rather than arriving in one lump",
            deltas.Count >= 4,
            $"only {deltas.Count} delta(s) — the parser may be buffering the whole body");

        Check(
            "Deltas concatenate to the same text that is returned",
            string.Concat(deltas) == result.Content,
            $"deltas={string.Concat(deltas)} vs content={result.Content}");

        Check(
            "Usage is read from the trailing usage-only frame",
            result.Usage.TokensIn == 120 && result.Usage.TokensOut == 18 && result.Usage.TokensCached == 80,
            $"in={result.Usage.TokensIn}, out={result.Usage.TokensOut}, cached={result.Usage.TokensCached}");

        Check(
            "The request asked for streaming and for usage to be included",
            handler.LastRequestedStream,
            "stream:true was not on the wire");

        // A reply cut off by the token limit must still be marked, and the marker has to reach
        // the incremental renderer too — otherwise the streamed bubble and the saved message
        // disagree about whether the answer was complete.
        const string truncated =
            """
            data: {"choices":[{"delta":{"content":"half a thou"},"finish_reason":"length"}]}

            data: [DONE]

            """;

        var cutHandler = new DribblingSseHandler(truncated, bytesPerRead: 5);
        var cutTransport = new OpenAiTransport(
            new HttpClient(cutHandler), "https://example.test", () => "key", "Gemini", supportsExplicitCache: false);

        var cutDeltas = new List<string>();
        var cut = await cutTransport.StreamAsync(request, d => cutDeltas.Add(d));

        Check(
            "A truncated stream is marked as cut off",
            cut.Content.Contains("[cut off"),
            cut.Content);

        Check(
            "...and the marker is streamed too, so the bubble matches what is saved",
            string.Concat(cutDeltas) == cut.Content,
            $"deltas={string.Concat(cutDeltas)} vs content={cut.Content}");

        // Some OpenAI-shaped gateways reject "stream"/"stream_options" outright. That must
        // degrade to a working non-streamed answer, not fail the student's turn.
        var rejectHandler = new DribblingSseHandler(
            sse, bytesPerRead: 7, firstStatus: HttpStatusCode.BadRequest);
        var rejectTransport = new OpenAiTransport(
            new HttpClient(rejectHandler), "https://example.test", () => "key", "Gemini", supportsExplicitCache: false);

        var fallbackDeltas = new List<string>();
        var fallback = await rejectTransport.StreamAsync(request, d => fallbackDeltas.Add(d));

        Check(
            "An endpoint that rejects streaming still returns an answer",
            fallback.Content == "fallback reply",
            $"got: {fallback.Content}");

        Check(
            "...delivered through the callback, so incremental callers need no special case",
            string.Concat(fallbackDeltas) == "fallback reply",
            $"deltas={string.Concat(fallbackDeltas)}");

        // Second call must not retry streaming — the rejection is remembered.
        var before = rejectHandler.Requests;
        await rejectTransport.StreamAsync(request, _ => { });

        Check(
            "A rejected endpoint is not asked to stream again",
            rejectHandler.Requests == before + 1 && !rejectHandler.LastRequestedStream,
            $"requests went {before} -> {rejectHandler.Requests}, lastStream={rejectHandler.LastRequestedStream}");
    }

    private static async Task TestTutorClientCachingCoverageAsync()
    {
        // SummarizePatternsAsync/GeneratePracticeAsync/GenerateWeaknessReviewAsync all run
        // on the same OpenAI-compatible chat transport ContinueThreadAsync already caches
        // successfully — they simply never asked for it. Confirms the fix reaches the
        // actual wire, not just that TutorClient compiles.
        const string canned =
            """{"choices":[{"message":{"content":"ok"}}],"usage":{"prompt_tokens":10,"completion_tokens":5}}""";

        var settings = new LlmSettings();
        var options = new TutorOptions();

        async Task<string?> BodyOfAsync(Func<TutorClient, Task> call)
        {
            var handler = new RecordingHttpMessageHandler(canned);
            var transport = new OpenAiTransport(
                new HttpClient(handler), "https://example.test", () => "key", "OpenAiCompatible", supportsExplicitCache: true);
            var client = new TutorClient(transport, transport, settings, options);
            await call(client);
            return handler.LastRequestBody;
        }

        var summaryBody = await BodyOfAsync(client => client.SummarizePatternsAsync(
            [new TutorFeedback { Label = "test", Severity = FeedbackSeverity.Minor }]));
        Check(
            "Pattern summary now caches its system prompt",
            summaryBody is not null
                && summaryBody.Contains("prompt_cache_key")
                && summaryBody.Contains("notetaker-pattern-summary"),
            summaryBody ?? "null");

        var practiceBody = await BodyOfAsync(client => client.GeneratePracticeAsync(
            new PracticeGenerationRequest("test section", [], 3)));
        Check(
            "Practice generation now caches its system prompt",
            practiceBody is not null
                && practiceBody.Contains("prompt_cache_key")
                && practiceBody.Contains("notetaker-practice-generation"),
            practiceBody ?? "null");

        var weaknessBody = await BodyOfAsync(client => client.GenerateWeaknessReviewAsync(
            [new TutorFeedback { Label = "dropped sign", Severity = FeedbackSeverity.Minor }],
            [new WeaknessTopic("sign errors", 2, FeedbackSeverity.Minor, "dropped sign")]));
        Check(
            "Weakness review now caches its system prompt",
            weaknessBody is not null
                && weaknessBody.Contains("prompt_cache_key")
                && weaknessBody.Contains("notetaker-weakness-review"),
            weaknessBody ?? "null");
    }

    private static async Task TestContinueThreadCachesHistoryAsync()
    {
        // A second+ chat turn should mark the last history message as a cache breakpoint
        // too, not just the system prompt — otherwise growing conversation history resends
        // in full, uncached, on every single turn.
        const string canned =
            """{"choices":[{"message":{"content":"ok"}}],"usage":{"prompt_tokens":10,"completion_tokens":5}}""";

        var handler = new RecordingHttpMessageHandler(canned);
        var transport = new OpenAiTransport(
            new HttpClient(handler), "https://example.test", () => "key", "OpenAiCompatible", supportsExplicitCache: true);
        var client = new TutorClient(transport, transport, new LlmSettings(), new TutorOptions());

        var history = new List<TutorMessage>
        {
            new() { Role = MessageRole.User, Content = "why is this wrong?" },
            new() { Role = MessageRole.Assistant, Content = "what happens to the sign here?" },
        };

        await client.ContinueThreadAsync(new TutorChatRequest(
            1, 1, history, "I flipped it", null, "dropped sign", Socratic: true, UserTurnCount: 2));

        var body = handler.LastRequestBody ?? string.Empty;
        var breakpointCount = body.Split("prompt_cache_breakpoint").Length - 1;
        Check(
            "A second chat turn caches history, not just the system prompt",
            breakpointCount >= 2,
            $"{breakpointCount} breakpoint(s) found");
    }

    private static void TestPromptsLoad()
    {
        Check("Live scan prompt loads", PromptLibrary.LiveScan.Length > 100, "empty");
        Check("Review scan prompt loads", PromptLibrary.ReviewScan.Length > 100, "empty");
        Check("Socratic prompt loads", PromptLibrary.Socratic.Length > 100, "empty");
        Check("Pattern summary prompt loads", PromptLibrary.PatternSummary.Length > 100, "empty");
        Check("Practice prompt loads", PromptLibrary.PracticeGeneration.Length > 100, "empty");
        Check("Weakness review prompt loads", PromptLibrary.WeaknessReview.Length > 100, "empty");

        Check(
            "Socratic prompt forbids leading with the answer",
            PromptLibrary.Socratic.Contains("question", StringComparison.OrdinalIgnoreCase)
                && PromptLibrary.Socratic.Contains("corrected step", StringComparison.OrdinalIgnoreCase),
            "no hint ladder wording");
    }

    private static TutorCoordinator BuildCoordinator(
        CountingTutorClient client,
        InkRepository ink,
        TutorRepository tutorRepo,
        UsageRepository usage,
        TestConnectivity connectivity,
        StubSnapshotProvider? snapshots = null) =>
        new(
            client,
            snapshots ?? new StubSnapshotProvider(),
            ink,
            tutorRepo,
            usage,
            connectivity,
            new TestClock(DateTimeOffset.UtcNow),
            new TutorOptions
            {
                LiveDebounce = TimeSpan.FromMilliseconds(120),
                MinLiveInterval = TimeSpan.Zero,
                MaxLiveCallsPerHour = 100,
                MonthlyCostCapUsd = 3000m,

                // Explicit because the shipped default is now off. These cases exist to prove
                // the scanning path still works for whoever turns it back on, so they have to
                // opt in — otherwise they would quietly pass by never running anything.
                VisionEnabled = true,
            });

    private static double Eval(string expression, double x) =>
        ExpressionEvaluator.Compile(expression)(x);

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1e-6;

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('-', title.Length));
    }

    private static void Check(string name, bool passed, string detail)
    {
        if (passed)
        {
            _passed++;
            Console.WriteLine($"  PASS  {name}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  FAIL  {name}  ({detail})");
        }
    }
}
