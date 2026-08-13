using System.Globalization;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace features.integration.tests.Seed;

/// <summary>
///     Creates a private, migrated database on the shared container for one seed test.
/// </summary>
/// <remarks>
///     The rest of this tier shares one database and owns its rows through fresh
///     <see cref="Guid" />s. <b>F00 cannot.</b> Its whole design is fixed primary keys and a globally
///     unique, <em>unfiltered</em> index on <c>attendance_codes.value</c>, so two seed tests in one
///     database would collide with each other and with any other test that ever writes a code called
///     <c>P</c> — and the collision would surface as a confusing failure in whichever test happened
///     to run second.
///     <para>
///         A second container was the alternative and is worse: another image start and another cold
///         start, to get what <c>CREATE DATABASE</c> gives for the cost of one migration. The
///         container stays shared; only the database is private.
///     </para>
///     <para>
///         Nothing is dropped afterwards. The container is disposed at the end of the run and takes
///         every database with it, and a cleanup path that runs after a failing test destroys the
///         state somebody would want to inspect.
///     </para>
/// </remarks>
internal static class SeedDatabase
{
    /// <summary>Creates and migrates a fresh database, returning a connection string for it.</summary>
    public static async Task<string> CreateAsync(string containerConnectionString, string label)
    {
        // Postgres identifiers fold to lower case unless quoted, and a name with a hyphen has to be
        // quoted everywhere afterwards. Built from a Guid so a re-run of the same test in the same
        // container cannot hit an existing name.
        string name = string.Create(
            CultureInfo.InvariantCulture,
            $"seed_{label.ToLowerInvariant()}_{Guid.NewGuid():N}");

        await using (NpgsqlConnection admin = new(containerConnectionString))
        {
            await admin.OpenAsync();

            // CREATE DATABASE takes no parameters and cannot run inside a transaction. The name is
            // built above from a literal label and a Guid, never from test input.
            await using NpgsqlCommand create = new($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        string connectionString = new NpgsqlConnectionStringBuilder(containerConnectionString)
        {
            Database = name
        }.ConnectionString;

        await using (SparkrockRwcDbContext dbContext = ContainerDbContextFactory.Create(connectionString))
            await dbContext.Database.MigrateAsync();

        return connectionString;
    }

    /// <summary>Runs a scalar query, for the assertions that must be the runbook's own SQL.</summary>
    public static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();

        await using NpgsqlCommand command = new(sql, connection);
        object? value = await command.ExecuteScalarAsync();

        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
}
