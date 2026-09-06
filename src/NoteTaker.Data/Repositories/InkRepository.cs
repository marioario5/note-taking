using NoteTaker.Core.Abstractions;
using NoteTaker.Core.Models;

namespace NoteTaker.Data.Repositories;

public sealed class InkRepository(NoteDatabase database) : IInkRepository
{
    public async Task<InkData?> LoadAsync(long pageId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT PageId, IsfBlob, Revision, UpdatedAt FROM InkData WHERE PageId = $id;";
        command.Parameters.AddWithValue("$id", pageId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new InkData
        {
            PageId = reader.GetInt64(0),
            IsfBlob = reader.GetBlob(1),
            Revision = reader.GetInt32(2),
            UpdatedAt = reader.GetTimestamp(3),
        };
    }

    public async Task<int> SaveAsync(long pageId, byte[] isfBlob, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // Upsert bumps the revision atomically so callers always learn the version
        // their strokes were stored under.
        command.CommandText = """
            INSERT INTO InkData (PageId, IsfBlob, Revision, UpdatedAt)
            VALUES ($pageId, $blob, 1, $updatedAt)
            ON CONFLICT(PageId) DO UPDATE SET
                IsfBlob   = excluded.IsfBlob,
                Revision  = InkData.Revision + 1,
                UpdatedAt = excluded.UpdatedAt;
            SELECT Revision FROM InkData WHERE PageId = $pageId;
            """;
        command.Parameters.AddWithValue("$pageId", pageId);
        command.Parameters.AddWithValue("$blob", isfBlob);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToSql());

        var revision = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(revision);
    }

    public async Task<long> SaveSnapshotAsync(
        long pageId,
        byte[] png,
        int strokeRevision,
        CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        return await connection.InsertAsync(
            """
            INSERT INTO PageSnapshot (PageId, PngBlob, StrokeRevision, CapturedAt)
            VALUES ($pageId, $png, $revision, $capturedAt)
            """,
            p =>
            {
                p.AddWithValue("$pageId", pageId);
                p.AddWithValue("$png", png);
                p.AddWithValue("$revision", strokeRevision);
                p.AddWithValue("$capturedAt", DateTimeOffset.UtcNow.ToSql());
            }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PageImage>> GetImagesAsync(long pageId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, PageId, Png, X, Y, W, H, CreatedAt FROM PageImage WHERE PageId = $id ORDER BY Id;";
        command.Parameters.AddWithValue("$id", pageId);

        var result = new List<PageImage>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new PageImage
            {
                Id = reader.GetInt64(0),
                PageId = reader.GetInt64(1),
                Png = reader.GetBlob(2),
                X = reader.GetDouble(3),
                Y = reader.GetDouble(4),
                Width = reader.GetDouble(5),
                Height = reader.GetDouble(6),
                CreatedAt = reader.GetTimestamp(7),
            });
        }

        return result;
    }

    public async Task<long> AddImageAsync(PageImage image, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        image.Id = await connection.InsertAsync(
            """
            INSERT INTO PageImage (PageId, Png, X, Y, W, H, CreatedAt)
            VALUES ($pageId, $png, $x, $y, $w, $h, $createdAt)
            """,
            p =>
            {
                p.AddWithValue("$pageId", image.PageId);
                p.AddWithValue("$png", image.Png);
                p.AddWithValue("$x", image.X);
                p.AddWithValue("$y", image.Y);
                p.AddWithValue("$w", image.Width);
                p.AddWithValue("$h", image.Height);
                p.AddWithValue("$createdAt", image.CreatedAt.ToSql());
            }, ct).ConfigureAwait(false);

        return image.Id;
    }

    public async Task UpdateImageBoundsAsync(PageImage image, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(
            "UPDATE PageImage SET X = $x, Y = $y, W = $w, H = $h WHERE Id = $id",
            p =>
            {
                p.AddWithValue("$x", image.X);
                p.AddWithValue("$y", image.Y);
                p.AddWithValue("$w", image.Width);
                p.AddWithValue("$h", image.Height);
                p.AddWithValue("$id", image.Id);
            }, ct).ConfigureAwait(false);
    }

    public async Task DeleteImageAsync(long imageId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(
            "DELETE FROM PageImage WHERE Id = $id",
            p => p.AddWithValue("$id", imageId), ct).ConfigureAwait(false);
    }

    public Task<PageSnapshot?> GetSnapshotAsync(long snapshotId, CancellationToken ct = default) =>
        GetSnapshotCoreAsync(
            "SELECT Id, PageId, PngBlob, StrokeRevision, CapturedAt FROM PageSnapshot WHERE Id = $id;",
            snapshotId,
            ct);

    public Task<PageSnapshot?> GetLatestSnapshotAsync(long pageId, CancellationToken ct = default) =>
        GetSnapshotCoreAsync(
            """
            SELECT Id, PageId, PngBlob, StrokeRevision, CapturedAt
              FROM PageSnapshot
             WHERE PageId = $id
             ORDER BY CapturedAt DESC
             LIMIT 1;
            """,
            pageId,
            ct);

    public async Task PruneSnapshotsAsync(long pageId, int keep, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(
            """
            DELETE FROM PageSnapshot
             WHERE PageId = $pageId
               AND Id NOT IN (
                   SELECT Id FROM PageSnapshot
                    WHERE PageId = $pageId
                    ORDER BY CapturedAt DESC
                    LIMIT $keep)
            """,
            p =>
            {
                p.AddWithValue("$pageId", pageId);
                p.AddWithValue("$keep", Math.Max(1, keep));
            }, ct).ConfigureAwait(false);
    }

    private async Task<PageSnapshot?> GetSnapshotCoreAsync(string sql, long id, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new PageSnapshot
        {
            Id = reader.GetInt64(0),
            PageId = reader.GetInt64(1),
            PngBlob = reader.GetBlob(2),
            StrokeRevision = reader.GetInt32(3),
            CapturedAt = reader.GetTimestamp(4),
        };
    }
}
