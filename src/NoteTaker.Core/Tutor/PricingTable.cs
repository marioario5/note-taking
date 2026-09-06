namespace NoteTaker.Core.Tutor;

public readonly record struct ModelPricing(
    decimal InputPerMillion,
    decimal OutputPerMillion,
    decimal CachedInputPerMillion,
    decimal CacheWritePerMillion);

/// <summary>
/// Published list prices per million tokens. These drift, so the table is a spending
/// estimate for the budget cap rather than an authoritative bill.
/// </summary>
public static class PricingTable
{
    private static readonly Dictionary<string, ModelPricing> Prices = new(StringComparer.OrdinalIgnoreCase)
    {
        // Gemini: cached input ~10% of input when implicit/explicit hits are reported.
        ["gemini-3.1-flash-lite"] = Gemini(0.25m, 1.50m),
        ["gemini-3.5-flash-lite"] = Gemini(0.30m, 2.50m),
        ["gemini-2.5-flash-lite"] = Gemini(0.10m, 0.40m),
        ["gemini-2.5-flash"] = Gemini(0.30m, 2.50m),
        ["gemini-3-flash-preview"] = Gemini(0.30m, 2.50m),
        // Pro tier: the chat model, deliberately priced above every Flash variant above. Chat
        // is where a stronger model actually pays off — arXiv 2501.07244 measured Pro-tier
        // Gemini at 0.77 on error CORRECTION against 0.66 for GPT-4o, and correction is the
        // half of the tutor's job that is explanation, not localization.
        // Checked against ai.google.dev/gemini-api/docs/pricing rather than assumed. The
        // previous 0.30/2.50 here was carried over by analogy from an older Flash model and
        // was wrong by 5x on input for the model actually running — the budget bar read a
        // fraction of real spend. Guessing a price by tier is how that happened; these are
        // transcribed.
        ["gemini-3.6-flash"] = Gemini(1.50m, 7.50m),
        ["gemini-3.5-flash"] = Gemini(1.50m, 9.00m),
        ["gemini-3.1-pro-preview"] = Gemini(2.00m, 12.00m),
        // Retired server-side (404s), but old ApiUsageLog rows still name it and must stay
        // priceable — dropping the row would silently re-cost past spend at zero.
        ["gemini-3-pro-preview"] = Gemini(1.25m, 5.00m),
        // OpenAI GPT-5.6: cache reads are cheap; writes are 1.25× input.
        ["gpt-5.6-luna"] = OpenAi(0.20m, 1.20m),
        ["gpt-5.6-terra"] = OpenAi(2.00m, 12.00m),
        ["gpt-5.6-sol"] = OpenAi(5.00m, 30.00m),
        ["gpt-4o-mini"] = OpenAi(0.15m, 0.60m, cacheWriteMultiplier: 1.0m),
        ["gpt-5-nano"] = OpenAi(0.05m, 0.40m, cacheWriteMultiplier: 1.0m),
        ["gpt-5-mini"] = OpenAi(0.25m, 2.00m, cacheWriteMultiplier: 1.0m),
        ["claude-haiku-4-5"] = new(1.00m, 5.00m, 0.10m, 1.25m),
        ["claude-sonnet-4-5"] = new(3.00m, 15.00m, 0.30m, 3.75m),
    };

    /// <summary>Falls back to a deliberately pessimistic price so unknown models cannot silently overspend.</summary>
    public static ModelPricing For(string model) =>
        Prices.TryGetValue(model, out var pricing)
            ? pricing
            : new ModelPricing(1.00m, 5.00m, 0.10m, 1.25m);

    public static decimal Estimate(string model, TokenUsage usage)
    {
        var pricing = For(model);
        var cached = Math.Min(usage.TokensCached, usage.TokensIn);
        var written = Math.Max(0, usage.TokensCacheWrite);
        var uncached = Math.Max(0, usage.TokensIn - cached);

        // Cache writes are billed in addition to (or instead of) the uncached path for
        // those tokens depending on the provider; we treat write tokens as write-rate only
        // and the remainder of input as uncached + cached hits.
        var writeOnly = Math.Min(written, uncached);
        var plainUncached = uncached - writeOnly;

        return (
                (plainUncached * pricing.InputPerMillion)
                + (cached * pricing.CachedInputPerMillion)
                + (writeOnly * pricing.CacheWritePerMillion)
                + (usage.TokensOut * pricing.OutputPerMillion))
               / 1_000_000m;
    }

    private static ModelPricing Gemini(decimal input, decimal output) =>
        new(input, output, input * 0.10m, input);

    private static ModelPricing OpenAi(decimal input, decimal output, decimal cacheWriteMultiplier = 1.25m) =>
        new(input, output, input * 0.10m, input * cacheWriteMultiplier);
}
