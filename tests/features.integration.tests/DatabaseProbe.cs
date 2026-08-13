using Npgsql;

namespace features.integration.tests;

/// <summary>
///     Reads the database directly, around EF Core.
/// </summary>
/// <remarks>
///     Two assertions in this tier are only meaningful outside EF: whether a row is <em>physically</em>
///     present after a soft delete (the query filter makes an EF read unable to tell "hidden" from
///     "gone"), and what the migration actually put in the catalogue. Both need a plain connection.
///     <para>
///         Going around EF rather than through <c>FromSqlRaw</c> is deliberate. Conventions §7 bans raw
///         SQL through the DbContext, and routing catalogue inspection past the ban rather than
///         through it keeps the ban meaning exactly what it says. All the SQL in this tier lives here,
///         in one file, with no user-supplied text anywhere near it.
///     </para>
/// </remarks>
internal static class DatabaseProbe
{
    public static async Task<T?> ScalarAsync<T>(
        string connectionString,
        string sql,
        params NpgsqlParameter[] parameters)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();

        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddRange(parameters);

        object? value = await command.ExecuteScalarAsync();

        return value is null or DBNull ? default : (T)value;
    }

    public static async Task<IReadOnlyList<string>> StringsAsync(
        string connectionString,
        string sql,
        params NpgsqlParameter[] parameters)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();

        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddRange(parameters);

        List<string> values = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
            values.Add(reader.GetString(0));

        return values;
    }
}
