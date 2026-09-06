using NoteTaker.Core.Abstractions;
using NoteTaker.Core.Models;
using NoteTaker.Core.Search;

namespace NoteTaker.App.Services;

/// <summary>
/// Keeps the search index current. Runs after a page is saved, off the input path so it
/// never competes with ink rendering.
/// </summary>
public sealed class IndexingService(
    IEmbeddingRepository embeddings,
    SearchService search,
    IEmbeddingModel model)
{
    public async Task IndexPageAsync(
        long pageId,
        byte[] pagePng,
        string recognizedText,
        int strokeRevision,
        CancellationToken ct = default)
    {
        var vector = await model.EmbedImageAsync(pagePng, ct).ConfigureAwait(false);

        await embeddings.UpsertAsync(
            new PageEmbedding
            {
                PageId = pageId,
                Vector = vector,
                RecognizedText = recognizedText,
                StrokeRevision = strokeRevision,
                UpdatedAt = DateTimeOffset.UtcNow,
            }, ct).ConfigureAwait(false);

        var related = await search.ComputeRelatedAsync(pageId, ct: ct).ConfigureAwait(false);
        await embeddings.ReplaceRelatedAsync(pageId, related, ct).ConfigureAwait(false);
    }
}
