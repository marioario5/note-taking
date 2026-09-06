using Microsoft.Data.Sqlite;
using NoteTaker.Core.Abstractions;
using NoteTaker.Core.Models;

namespace NoteTaker.Data.Repositories;

public sealed class PageRepository(NoteDatabase database) : IPageRepository
{
    private const string SelectColumns =
        "SELECT Id, SectionId, Title, SortOrder, TutorMode, PdfPath, PdfPageIndex, UpdatedAt FROM Page";

    public async Task<IReadOnlyList<Page>> GetPagesAsync(long sectionId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} WHERE SectionId = $id ORDER BY SortOrder, Id;";
        command.Parameters.AddWithValue("$id", sectionId);
        return await ReadPagesAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Page>> GetAllPagesAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} ORDER BY Id;";
        return await ReadPagesAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<Page?> GetPageAsync(long pageId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", pageId);
        var pages = await ReadPagesAsync(command, ct).ConfigureAwait(false);
        return pages.Count > 0 ? pages[0] : null;
    }

    public async Task<Page> CreatePageAsync(long sectionId, string title, CancellationToken ct = default)
    {
        var page = new Page { SectionId = sectionId, Title = title, UpdatedAt = DateTimeOffset.UtcNow };
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        page.Id = await connection.InsertAsync(
            """
            INSERT INTO Page (SectionId, Title, SortOrder, TutorMode, UpdatedAt)
            VALUES ($sectionId, $title,
                    (SELECT COALESCE(MAX(SortOrder) + 1, 0) FROM Page WHERE SectionId = $sectionId),
                    $tutorMode, $updatedAt)
            """,
            p =>
            {
                p.AddWithValue("$sectionId", sectionId);
                p.AddWithValue("$title", title);
                p.AddWithValue("$tutorMode", (int)page.TutorMode);
                p.AddWithValue("$updatedAt", page.UpdatedAt.ToSql());
            }, ct).ConfigureAwait(false);

        return page;
    }

    public async Task UpdatePageAsync(Page page, CancellationToken ct = default)
    {
        page.UpdatedAt = DateTimeOffset.UtcNow;
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(
            """
            UPDATE Page
               SET Title = $title,
                   SortOrder = $sortOrder,
                   TutorMode = $tutorMode,
                   PdfPath = $pdfPath,
                   PdfPageIndex = $pdfPageIndex,
                   UpdatedAt = $updatedAt
             WHERE Id = $id
            """,
            p =>
            {
                p.AddWithValue("$title", page.Title);
                p.AddWithValue("$sortOrder", page.SortOrder);
                p.AddWithValue("$tutorMode", (int)page.TutorMode);
                p.AddWithValue("$pdfPath", (object?)page.PdfPath ?? DBNull.Value);
                p.AddWithValue("$pdfPageIndex", (object?)page.PdfPageIndex ?? DBNull.Value);
                p.AddWithValue("$updatedAt", page.UpdatedAt.ToSql());
                p.AddWithValue("$id", page.Id);
            }, ct).ConfigureAwait(false);
    }

    public async Task DeletePageAsync(long pageId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(
            "DELETE FROM Page WHERE Id = $id",
            p => p.AddWithValue("$id", pageId), ct).ConfigureAwait(false);
    }

    private static async Task<List<Page>> ReadPagesAsync(SqliteCommand command, CancellationToken ct)
    {
        var result = new List<Page>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new Page
            {
                Id = reader.GetInt64(0),
                SectionId = reader.GetInt64(1),
                Title = reader.GetString(2),
                SortOrder = reader.GetInt32(3),
                TutorMode = (TutorMode)reader.GetInt32(4),
                PdfPath = reader.GetNullableString(5),
                PdfPageIndex = reader.GetNullableInt32(6),
                UpdatedAt = reader.GetTimestamp(7),
            });
        }

        return result;
    }
}
