using NoteTaker.Core.Abstractions;
using NoteTaker.Core.Models;

namespace NoteTaker.Data.Repositories;

public sealed class NotebookRepository(NoteDatabase database) : INotebookRepository
{
    public async Task<IReadOnlyList<Notebook>> GetNotebooksAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, CreatedAt FROM Notebook ORDER BY Name;";

        var result = new List<Notebook>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new Notebook
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                CreatedAt = reader.GetTimestamp(2),
            });
        }

        return result;
    }

    public async Task<IReadOnlyList<Section>> GetSectionsAsync(long notebookId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, NotebookId, Name, SortOrder FROM Section WHERE NotebookId = $id ORDER BY SortOrder, Name;";
        command.Parameters.AddWithValue("$id", notebookId);

        var result = new List<Section>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new Section
            {
                Id = reader.GetInt64(0),
                NotebookId = reader.GetInt64(1),
                Name = reader.GetString(2),
                SortOrder = reader.GetInt32(3),
            });
        }

        return result;
    }

    public async Task<Notebook> CreateNotebookAsync(string name, CancellationToken ct = default)
    {
        var notebook = new Notebook { Name = name, CreatedAt = DateTimeOffset.UtcNow };
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        notebook.Id = await connection.InsertAsync(
            "INSERT INTO Notebook (Name, CreatedAt) VALUES ($name, $createdAt)",
            p =>
            {
                p.AddWithValue("$name", notebook.Name);
                p.AddWithValue("$createdAt", notebook.CreatedAt.ToSql());
            }, ct).ConfigureAwait(false);

        return notebook;
    }

    public async Task<Section> CreateSectionAsync(long notebookId, string name, CancellationToken ct = default)
    {
        var section = new Section { NotebookId = notebookId, Name = name };
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        section.Id = await connection.InsertAsync(
            """
            INSERT INTO Section (NotebookId, Name, SortOrder)
            VALUES ($notebookId, $name, (SELECT COALESCE(MAX(SortOrder) + 1, 0) FROM Section WHERE NotebookId = $notebookId))
            """,
            p =>
            {
                p.AddWithValue("$notebookId", notebookId);
                p.AddWithValue("$name", name);
            }, ct).ConfigureAwait(false);

        return section;
    }

    public async Task RenameNotebookAsync(long id, string name, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(
            "UPDATE Notebook SET Name = $name WHERE Id = $id",
            p =>
            {
                p.AddWithValue("$name", name);
                p.AddWithValue("$id", id);
            }, ct).ConfigureAwait(false);
    }

    public async Task RenameSectionAsync(long id, string name, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(
            "UPDATE Section SET Name = $name WHERE Id = $id",
            p =>
            {
                p.AddWithValue("$name", name);
                p.AddWithValue("$id", id);
            }, ct).ConfigureAwait(false);
    }

    public async Task DeleteNotebookAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(
            "DELETE FROM Notebook WHERE Id = $id",
            p => p.AddWithValue("$id", id), ct).ConfigureAwait(false);
    }

    public async Task DeleteSectionAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await connection.ExecuteAsync(
            "DELETE FROM Section WHERE Id = $id",
            p => p.AddWithValue("$id", id), ct).ConfigureAwait(false);
    }
}
