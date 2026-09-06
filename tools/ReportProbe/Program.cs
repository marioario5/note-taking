using System.Diagnostics;
using System.Net.Http;
using NoteTaker.AI;
using NoteTaker.App.Services;
using NoteTaker.Core.Models;
using NoteTaker.Core.Tutor;
using NoteTaker.Data;
using NoteTaker.Data.Repositories;

namespace NoteTaker.ReportProbe;

/// <summary>
/// Writes one real study report and prints what it cost.
/// </summary>
/// <remarks>
/// Exists because the report's cost and quality could only be judged from a stored row after the
/// fact, and that row is written by a button the student presses. The first such row billed 896
/// output tokens for forty visible words, which is a question about thinking tokens the database
/// cannot answer — <c>TokensOut</c> is the sum of visible and thinking, and only a live call
/// shows the split.
///
/// The API key is read in-process from the Windows Credential Manager, exactly as the app reads
/// it, so measuring the prompt never means handling the secret.
///
/// This SPENDS. One run is one report against the real topic, billed to the real cap.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var sectionId = args.Length > 0 && long.TryParse(args[0], out var id) ? id : 36;

        var settings = AppSettings.Load();
        var database = new NoteDatabase(NoteDatabase.DefaultPath);
        var tutorRepo = new TutorRepository(database);

        var events = await tutorRepo.GetSkillEventsAsync(sectionId, DateTimeOffset.MinValue);
        if (events.Count == 0)
        {
            Console.WriteLine($"section {sectionId} has no skill events; nothing to report on.");
            return 1;
        }

        var topic = SkillConfidence.Score(events, DateTimeOffset.UtcNow);

        Console.WriteLine($"section {sectionId}: {topic.TotalAttempts} attempts, {topic.Skills.Count} skills");
        foreach (var skill in topic.Skills)
        {
            Console.WriteLine(
                $"   {skill.Skill,-24} attempts {skill.Attempts,2}  corrections {skill.Corrections,2}  "
                + $"independence {skill.Confidence:0.00}  {skill.Trend.ToString().ToLowerInvariant()}");
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var visionHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };

        var client = TutorClientFactory.Create(
            visionHttp,
            http,
            settings.ToLlmSettings(),
            new TutorOptions(),
            new CredentialStore());

        var watch = Stopwatch.StartNew();
        var result = await client.GenerateSkillReportAsync("5.6 Center of Mass", topic);
        watch.Stop();

        // Logged like any other call. Measuring the tutor spends the same real money the tutor
        // does, and four probe runs that never reached the ledger left the usage screen quietly
        // understating the month by the cost of the thing being measured.
        var cost = PricingTable.Estimate(result.Model, result.Usage);
        await new UsageRepository(database).LogAsync(new ApiUsageLog
        {
            CallType = TutorCallType.SkillReport,
            Model = result.Model,
            TokensIn = result.Usage.TokensIn,
            TokensOut = result.Usage.TokensOut,
            CostEstimate = cost,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var words = result.Content.Split(
            [' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;

        Console.WriteLine();
        Console.WriteLine("──── report ────");
        Console.WriteLine(result.Content);
        Console.WriteLine("────────────────");
        Console.WriteLine();
        Console.WriteLine($"model        {result.Model}");
        Console.WriteLine($"tokens in    {result.Usage.TokensIn}");
        Console.WriteLine($"tokens out   {result.Usage.TokensOut}   (visible + thinking)");
        Console.WriteLine($"visible      ~{words} words, {result.Content.Length} chars");
        Console.WriteLine($"elapsed      {watch.Elapsed.TotalSeconds:0.0}s");

        Console.WriteLine($"cost         ${cost:0.00000}   (logged to the usage ledger)");
        Console.WriteLine($"month cap    {cost / 8.00m * 100:0.00}% of the $8 cap");
        return 0;
    }
}
