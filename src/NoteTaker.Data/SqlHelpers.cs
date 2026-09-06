using System.Globalization;
using Microsoft.Data.Sqlite;

namespace NoteTaker.Data;

internal static class SqlHelpers
{
    /// <summary>Round-trippable ISO 8601 so ordering works lexicographically in SQL.</summary>
    public static string ToSql(this DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset GetTimestamp(this SqliteDataReader reader, int ordinal) =>
        DateTimeOffset.Parse(
            reader.GetString(ordinal),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    public static string? GetNullableString(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static long? GetNullableInt64(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    public static int? GetNullableInt32(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    public static byte[] GetBlob(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? [] : (byte[])reader.GetValue(ordinal);

    public static async Task<long> InsertAsync(
        this SqliteConnection connection,
        string sql,
        Action<SqliteParameterCollection> bind,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql + "; SELECT last_insert_rowid();";
        bind(command.Parameters);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public static async Task<int> ExecuteAsync(
        this SqliteConnection connection,
        string sql,
        Action<SqliteParameterCollection>? bind,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind?.Invoke(command.Parameters);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
