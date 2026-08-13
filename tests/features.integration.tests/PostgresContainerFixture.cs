using domain.Security;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace features.integration.tests;

/// <summary>
///     One PostgreSQL container per test collection, migrated once.
/// </summary>
/// <remarks>
///     <b>There is no port conflict with Aspire, and this is the file where people look for one.</b>
///     The AppHost pins its persistent Postgres to host port 5433; this container publishes 5432 to a
///     random ephemeral host port chosen by the Docker daemon, and <c>WithHostPort</c> is deliberately
///     never called. The two run side by side (VC-24). The 5433 in the AppHost is a DCP proxy that
///     exists only while the AppHost runs — not a reservation Testcontainers could trip over. Pinning
///     a host port here would <em>create</em> the conflict everyone assumes already exists.
///     <para>
///         The migration runs once, in <see cref="InitializeAsync" />, through
///         <see cref="ContainerDbContextFactory" /> — the same option chain the tests use, so the
///         schema cannot be created under one naming convention and queried under another.
///     </para>
///     <para>
///         <b>The database is shared by every test in the collection.</b> Tests own their rows through
///         fresh <see cref="Guid" />s; none may assume an empty table and none truncates. A database
///         per test class would remove the hazard at the cost of a migration run per class — the
///         fixture exists to pay that once.
///     </para>
///     <para>
///         The resource reaper (Ryuk) is left enabled. Its image pull is part of VC-24's ~66s cold
///         start, but it is a one-off per machine, and disabling it leaves containers behind after
///         every cancelled run.
///     </para>
/// </remarks>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    /// <summary>
    ///     Matches the PostgreSQL 17 the VC-xx constraints were verified against. The tag floats
    ///     within major 17 on purpose: a digest pin is reproducible and goes stale silently, because
    ///     nobody updates a digest until something breaks.
    /// </summary>
    private const string PostgresImage = "postgres:17-alpine";

    // The image goes through the constructor: the parameterless PostgreSqlBuilder() is obsolete in
    // 4.13.0 and using it with WithImage() is a CS0618, which TreatWarningsAsErrors turns into a
    // build failure.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(PostgresImage).Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        // Before StartAsync, so an absent daemon fails in seconds with an actionable message rather
        // than as an opaque socket error from inside container startup.
        await DockerAvailability.EnsureAvailableAsync();

        await _container.StartAsync();

        await using SparkrockRwcDbContext dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <inheritdoc cref="ContainerDbContextFactory.Create" />
    /// <remarks>
    ///     <c>internal</c> because <see cref="SparkrockRwcDbContext" /> is: the fixture type itself has
    ///     to be public for xUnit to construct it, but a public method cannot return a less accessible
    ///     type (CS0050). Tests in this assembly reach it either way.
    /// </remarks>
    internal SparkrockRwcDbContext CreateDbContext(
        TimeProvider? clock = null,
        ICurrentUser? currentUser = null,
        IAuditOverride? auditOverride = null)
        => ContainerDbContextFactory.Create(ConnectionString, clock, currentUser, auditOverride);
}
