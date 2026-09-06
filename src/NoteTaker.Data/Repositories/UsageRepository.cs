using NoteTaker.Core.Abstractions;
using NoteTaker.Core.Models;
using NoteTaker.Core.Tutor;

namespace NoteTaker.Data.Repositories;

public sealed class UsageRepository(NoteDatabase database) : IUsageRepository
{
    public async Task LogAsync(ApiUsageLog entry, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        entry.Id = await connection.InsertAsync(
            """
            INSERT INTO ApiUsageLog
                (CallType, Model, TokensIn, TokensOut, TokensCached, TokensCacheWrite, CostEstimate, CreatedAt)
            VALUES
                ($callType, $model, $tokensIn, $tokensOut, $tokensCached, $tokensCacheWrite, $cost, $createdAt)
            """,
            p =>
            {
                p.AddWithValue("$callType", (int)entry.CallType);
                p.AddWithValue("$model", entry.Model);
                p.AddWithValue("$tokensIn", entry.TokensIn);
                p.AddWithValue("$tokensOut", entry.TokensOut);
                p.AddWithValue("$tokensCached", entry.TokensCached);
                p.AddWithValue("$tokensCacheWrite", entry.TokensCacheWrite);
                p.AddWithValue("$cost", (double)entry.CostEstimate);
                p.AddWithValue("$createdAt", entry.CreatedAt.ToSql());
            }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<DateTimeOffset, SpendSplit>> GetDailyCostsAsync(
        DateTimeOffset since,
        CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // Every row since the cutoff, bucketed in C# rather than in SQL: the budget day is
        // Pacific midnight, and SQLite's date functions know nothing about that zone — grouping
        // by a SQL date string would file an evening's work under the following day.
        command.CommandText = """
            SELECT CreatedAt, CostEstimate, CallType
              FROM ApiUsageLog
             WHERE CreatedAt >= $since;
            """;
        command.Parameters.AddWithValue("$since", since.ToSql());

        var byDay = new Dictionary<DateTimeOffset, SpendSplit>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var at = DateTimeOffset.Parse(
                reader.GetString(0),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind);

            var day = BudgetDay.StartOf(at);
            byDay.TryGetValue(day, out var running);
            byDay[day] = running.Add((TutorCallType)reader.GetInt32(2), reader.GetDecimal(1));
        }

        return byDay;
    }

    public async Task<UsageSummary> GetSummaryAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(TokensIn), 0),
                   COALESCE(SUM(TokensOut), 0),
                   COALESCE(SUM(CostEstimate), 0),
                   COALESCE(SUM(TokensCached), 0)
              FROM ApiUsageLog
             WHERE CreatedAt >= $since;
            """;
        command.Parameters.AddWithValue("$since", since.ToSql());

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return UsageSummary.Empty;
        }

        return new UsageSummary(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            (decimal)reader.GetDouble(3),
            reader.GetInt32(4));
    }

    public async Task<IReadOnlyList<UsageBreakdownRow>> GetBreakdownAsync(
        DateTimeOffset since,
        CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CallType,
                   Model,
                   COUNT(*),
                   COALESCE(SUM(TokensIn), 0),
                   COALESCE(SUM(TokensOut), 0),
                   COALESCE(SUM(TokensCached), 0),
                   COALESCE(SUM(CostEstimate), 0)
              FROM ApiUsageLog
             WHERE CreatedAt >= $since
             GROUP BY CallType, Model
             ORDER BY 7 DESC;
            """;
        command.Parameters.AddWithValue("$since", since.ToSql());

        var rows = new List<UsageBreakdownRow>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new UsageBreakdownRow(
                (TutorCallType)reader.GetInt32(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                (decimal)reader.GetDouble(6)));
        }

        return rows;
    }
}
