namespace NoteTaker.Core.Models;

public enum SearchMatchKind
{
    /// <summary>Matched recognized ink text or PDF text.</summary>
    Keyword = 0,

    /// <summary>Matched by CLIP page-image similarity.</summary>
    Visual = 1,
}

public sealed record SearchResult(
    long PageId,
    string PageTitle,
    string NotebookName,
    string SectionName,
    double Score,
    SearchMatchKind Kind,
    string? Snippet);
