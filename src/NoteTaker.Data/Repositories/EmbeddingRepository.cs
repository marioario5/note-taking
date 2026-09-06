using NoteTaker.Core.Abstractions;
using NoteTaker.Core.Models;
using NoteTaker.Core.Search;

namespace NoteTaker.Data.Repositories;

public sealed class EmbeddingRepository(NoteDatabase database) : IEmbeddingRepository, ISearchRepository
{
    public async Task UpsertAsync(PageEmbedding embedding, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(
            """
            INSERT INTO PageEmbedding (PageId, Vector, RecognizedText, StrokeRevision, UpdatedAt)
            VALUES ($pageId, $vector, $text, $revision, $updatedAt)
            ON CONFLICT(PageId) DO UPDATE SET
                Vector         = excluded.Vector,
                RecognizedText = excluded.RecognizedText,
                StrokeRevision = excluded.StrokeRevision,
                UpdatedAt      = excluded.UpdatedAt
            """,
            p =>
            {
                p.AddWithValue("$pageId", embedding.PageId);
                p.AddWithValue("$vector", VectorMath.ToBytes(embedding.Vector));
                p.AddWithValue("$text", embedding.RecognizedText);
                p.AddWithValue("$revision", embedding.StrokeRevision);
                p.AddWithValue("$updatedAt", embedding.UpdatedAt.ToSql());
            }, ct).ConfigureAwait(false);
    }

    public async Task<PageEmbedding?> GetAsync(long pageId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT PageId, Vector, RecognizedText, StrokeRevision, UpdatedAt FROM PageEmbedding WHERE PageId = $id;";
        command.Parameters.AddWithValue("$id", pageId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new PageEmbedding
        {
            PageId = reader.GetInt64(0),
            Vector = VectorMath.FromBytes(reader.GetBlob(1)),
            RecognizedText = reader.GetString(2),
            StrokeRevision = reader.GetInt32(3),
            UpdatedAt = reader.GetTimestamp(4),
        };
    }

    public async Task<IReadOnlyList<PageEmbedding>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT PageId, Vector, RecognizedText, StrokeRevision, UpdatedAt FROM PageEmbedding;";

        var result = new List<PageEmbedding>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new PageEmbedding
            {
                PageId = reader.GetInt64(0),
                Vector = VectorMath.FromBytes(reader.GetBlob(1)),
                RecognizedText = reader.GetString(2),
                StrokeRevision = reader.GetInt32(3),
                UpdatedAt = reader.GetTimestamp(4),
            });
        }

        return result;
    }

    public async Task ReplaceRelatedAsync(
        long sourcePageId,
        IReadOnlyList<RelatedPage> related,
        CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)
            await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM RelatedPage WHERE SourcePageId = $id;";
            delete.Parameters.AddWithValue("$id", sourcePageId);
            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var item in related)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO RelatedPage (SourcePageId, TargetPageId, Score) VALUES ($source, $target, $score);";
            insert.Parameters.AddWithValue("$source", item.SourcePageId);
            insert.Parameters.AddWithValue("$target", item.TargetPageId);
            insert.Parameters.AddWithValue("$score", item.Score);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RelatedPage>> GetRelatedAsync(long pageId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT SourcePageId, TargetPageId, Score FROM RelatedPage WHERE SourcePageId = $id ORDER BY Score DESC;";
        command.Parameters.AddWithValue("$id", pageId);

        var result = new List<RelatedPage>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new RelatedPage
            {
                SourcePageId = reader.GetInt64(0),
                TargetPageId = reader.GetInt64(1),
                Score = reader.GetDouble(2),
            });
        }

        return result;
    }

    /// <summary>Flattened index joining page titles with their embeddings and recognized ink.</summary>
    public async Task<IReadOnlyList<PageIndexEntry>> GetIndexAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.Id,
                   p.Title,
                   s.Name,
                   n.Name,
                   COALESCE(e.RecognizedText, ''),
                   e.Vector
              FROM Page p
              JOIN Section  s ON s.Id = p.SectionId
              JOIN Notebook n ON n.Id = s.NotebookId
              LEFT JOIN PageEmbedding e ON e.PageId = p.Id
             ORDER BY p.Id;
            """;

        var result = new List<PageIndexEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new PageIndexEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                VectorMath.FromBytes(reader.GetBlob(5))));
        }

        return result;
    }
}
