using NoteTaker.Core.Models;

namespace NoteTaker.Core.Search;

/// <summary>One row of the flattened page index, joined across notebook and section.</summary>
public sealed record PageIndexEntry(
    long PageId,
    string PageTitle,
    string SectionName,
    string NotebookName,
    string RecognizedText,
    float[] Vector);

public interface ISearchRepository
{
    Task<IReadOnlyList<PageIndexEntry>> GetIndexAsync(CancellationToken ct = default);
}

/// <summary>
/// Two deliberately separate capabilities, matching what handwritten STEM notes can
/// actually support: keyword lookup over recognized words, and visual similarity over
/// whole pages. There is no pretence of searching inside handwritten equations.
/// </summary>
public sealed class SearchService(ISearchRepository repository)
{
    public async Task<IReadOnlyList<SearchResult>> SearchKeywordAsync(
        string query,
        int limit = 25,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var index = await repository.GetIndexAsync(ct).ConfigureAwait(false);
        var results = new List<SearchResult>();

        foreach (var entry in index)
        {
            var score = ScoreEntry(entry, terms);
            if (score <= 0)
            {
                continue;
            }

            results.Add(new SearchResult(
                entry.PageId,
                entry.PageTitle,
                entry.NotebookName,
                entry.SectionName,
                score,
                SearchMatchKind.Keyword,
                BuildSnippet(entry.RecognizedText, terms)));
        }

        return results.OrderByDescending(r => r.Score).Take(limit).ToList();
    }

    /// <summary>Ranks other pages by CLIP image similarity to the given page.</summary>
    public async Task<IReadOnlyList<SearchResult>> FindSimilarPagesAsync(
        long pageId,
        int limit = 10,
        double minimumScore = 0.55,
        CancellationToken ct = default)
    {
        var index = await repository.GetIndexAsync(ct).ConfigureAwait(false);
        var source = index.FirstOrDefault(e => e.PageId == pageId);
        if (source is null || source.Vector.Length == 0)
        {
            return [];
        }

        return index
            .Where(e => e.PageId != pageId && e.Vector.Length == source.Vector.Length)
            .Select(e => new
            {
                Entry = e,
                Score = VectorMath.Similarity(source.Vector, e.Vector),
            })
            .Where(x => x.Score >= minimumScore)
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .Select(x => new SearchResult(
                x.Entry.PageId,
                x.Entry.PageTitle,
                x.Entry.NotebookName,
                x.Entry.SectionName,
                x.Score,
                SearchMatchKind.Visual,
                null))
            .ToList();
    }

    public async Task<IReadOnlyList<RelatedPage>> ComputeRelatedAsync(
        long pageId,
        int topN = 5,
        double minimumScore = 0.55,
        CancellationToken ct = default)
    {
        var similar = await FindSimilarPagesAsync(pageId, topN, minimumScore, ct).ConfigureAwait(false);
        return similar
            .Select(s => new RelatedPage { SourcePageId = pageId, TargetPageId = s.PageId, Score = s.Score })
            .ToList();
    }

    private static double ScoreEntry(PageIndexEntry entry, string[] terms)
    {
        double score = 0;
        foreach (var term in terms)
        {
            if (entry.PageTitle.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                // Titles are typed, so a title hit is far more reliable than recognized ink.
                score += 3;
            }

            if (entry.RecognizedText.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 1;
            }
        }

        return score;
    }

    private static string? BuildSnippet(string text, string[] terms)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var term in terms)
        {
            var index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var start = Math.Max(0, index - 40);
            var length = Math.Min(text.Length - start, 120);
            return (start > 0 ? "…" : string.Empty) + text.Substring(start, length).Trim();
        }

        return text.Length <= 120 ? text : text[..120];
    }
}
