using System.Collections.Concurrent;
using System.Reflection;

namespace NoteTaker.AI;

/// <summary>Loads the versioned system prompts embedded from the repo's prompts folder.</summary>
public static class PromptLibrary
{
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    public static string LiveScan => Get("live-scan");
    public static string ReviewScan => Get("review-scan");
    public static string Socratic => Get("socratic");
    public static string PatternSummary => Get("pattern-summary");
    public static string PracticeGeneration => Get("practice-generation");
    public static string WeaknessReview => Get("weakness-review");
    public static string SkillReport => Get("skill-report");

    public static string SkillCheck => Get("skill-check");

    public static string Get(string name) => Cache.GetOrAdd(name, static key =>
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream($"prompts/{key}.md")
            ?? throw new InvalidOperationException($"Prompt '{key}' was not embedded in the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });
}
