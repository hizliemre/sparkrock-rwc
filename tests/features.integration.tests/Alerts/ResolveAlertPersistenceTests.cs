using domain.Alerts;
using domain.Exceptions;
using domain.Schools;
using domain.Students;
using features.Alerts;
using features.integration.tests.Fakes;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace features.integration.tests.Alerts;

/// <summary>
///     The two F10 assertions that only a real database can carry: the shape of the resolution
///     columns, and that a resolution actually frees the episode slot.
/// </summary>
/// <remarks>
///     Neither is expressible on EF InMemory — it has no schema and enforces no unique index, let
///     alone a partial one — and neither is written at the handler tier (conventions §6's tier rule).
///     <para>
///         The database is shared across the collection: this class allocates fresh
///         <see cref="Guid" />s and school years and asserts only about its own rows.
///     </para>
/// </remarks>
[Collection(IntegrationTestCollectionDefinition.Name)]
public sealed class ResolveAlertPersistenceTests(PostgresContainerFixture fixture)
{
    private const string Reason = "Home visit completed; attendance plan agreed with the family.";

    /// <summary>
    ///     <b>V-22's <c>Verified by</c>, and O-34's.</b> Both halves are required.
    /// </summary>
    /// <remarks>
    ///     V-22 promised <c>ResolvedBy</c> would become "<c>Guid?</c> plus <c>LegacyResolvedBy</c>".
    ///     V-18 says summaries and alerts are recomputed and <b>never imported</b>, so no code path
    ///     could ever write a legacy username here — and L-07 records that nothing in the supplied
    ///     legacy code writes <c>ResolvedBy</c> at all, so the column would migrate a column with no
    ///     values. O-34 resolves the contradiction by dropping <c>LegacyResolvedBy</c>; this test is
    ///     what makes the drop a fact rather than a note. A test asserting only the <c>uuid</c> half
    ///     would pass with the contradictory column present.
    ///     <para>
    ///         Read through <see cref="DatabaseProbe" />, a plain <see cref="NpgsqlConnection" />,
    ///         rather than <c>FromSqlRaw</c>: conventions §7 bans raw SQL through the DbContext, and
    ///         routing catalogue inspection past the ban rather than through it keeps the ban meaning
    ///         what it says.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task Schema_StudentAlertsResolvedByIsUuidAndHasNoLegacyResolvedByColumn()
    {
        IReadOnlyList<string> columns = await DatabaseProbe.StringsAsync(
            fixture.ConnectionString,
            "SELECT column_name || ':' || data_type FROM information_schema.columns "
            + "WHERE table_schema = 'public' AND table_name = 'student_alerts' ORDER BY column_name");

        Assert.NotEmpty(columns);
        Assert.Contains("resolved_by:uuid", columns, StringComparer.Ordinal);

        Assert.DoesNotContain(
            columns,
            column => column.StartsWith("legacy_resolved_by:", StringComparison.Ordinal));
    }

    /// <summary>
    ///     <b>V-08's F10 <c>Verified by</c>.</b> Legacy raised alerts and never resolved them (L-07);
    ///     this proves the other half of the lifecycle reaches the database and frees the slot.
    /// </summary>
    /// <remarks>
    ///     The partial unique index filters <c>resolved_at IS NULL AND is_deleted = false</c>. If the
    ///     <c>resolved_at IS NULL</c> term were ever dropped, a resolved episode would keep occupying
    ///     the slot and DEC-18's re-raise — a <em>new episode row</em> for the same key — would fail
    ///     with <c>23505</c> forever. EF InMemory enforces no index at all, so this assertion is
    ///     vacuous anywhere but here.
    /// </remarks>
    [Fact]
    public async Task Resolve_WhenEpisodeResolved_AllowsANewEpisodeForTheSameKey()
    {
        const int schoolYearStart = 2081;

        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();
        School school = await AlertFixture.SchoolAsync(dbContext, threshold: 10);
        Student student = await AlertFixture.StudentAsync(dbContext, school.Id);
        StudentAlert first = await AlertFixture.OpenAlertAsync(
            dbContext, student.Id, school.Id, schoolYearStart);

        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(school.Id);

        ResolveAlert.CommandHandler handler = new(
            dbContext,
            caller,
            new FakeTimeProvider(ContainerDbContextFactory.DefaultNow),
            NullLogger<ResolveAlert.CommandHandler>.Instance);

        await handler.Handle(
            new ResolveAlert.Command { AlertId = first.Id, Reason = Reason },
            CancellationToken.None);

        // The identical key: same student, same type, same year, same school.
        StudentAlert reRaised = AlertFixture.NewOpenAlert(
            student.Id, school.Id, schoolYearStart, thresholdAtRaise: 10);

        dbContext.StudentAlerts.Add(reRaised);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        StudentAlert[] persisted = await dbContext.StudentAlerts
            .AsNoTracking()
            .Where(alert => alert.StudentId == student.Id)
            .OrderBy(alert => alert.ResolvedAt)
            .ToArrayAsync(CancellationToken.None);

        Assert.Equal(2, persisted.Length);
        Assert.Single(persisted, alert => alert.ResolvedAt is null);
        Assert.Single(
            persisted,
            alert => alert.ResolvedAt is not null && alert.ResolutionSource == ResolutionSource.Manual);
    }

    /// <summary>
    ///     The negative direction, so the test above cannot pass because the index is absent
    ///     altogether.
    /// </summary>
    /// <remarks>
    ///     Asserts <see cref="ConcurrencyConflictException" />, not <see cref="ConflictException" />.
    ///     DEC-18 and O-52 make this constraint <b>retryable</b> — mapped to a bare 409 it fails a
    ///     whole 28-student attendance batch on one racing student — and
    ///     <c>ConstraintErrorTranslator</c> therefore produces the retryable type, which deliberately
    ///     does not derive from <see cref="ConflictException" /> so F07's retry predicate can tell the
    ///     two apart. F10's tasks.md predates O-52 and names the other type; the code and DEC-18
    ///     agree, so the task list is the losing side.
    ///     <para>
    ///         F10 never inserts an alert, so this code is not one of F10's — it is asserted here
    ///         because it is the guard that gives the previous test its meaning.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task Resolve_WhenTwoOpenEpisodesForTheSameKeyInserted_IsRejectedByTheIndex()
    {
        const int schoolYearStart = 2082;

        // Its own context: ContainerDbContextFactory supplies no constraint-error registry, so a
        await using SparkrockRwcDbContext dbContext = ContainerDbContextFactory.Create(fixture.ConnectionString);
        School school = await AlertFixture.SchoolAsync(dbContext, threshold: 10);
        Student student = await AlertFixture.StudentAsync(dbContext, school.Id);

        dbContext.StudentAlerts.Add(
            AlertFixture.NewOpenAlert(student.Id, school.Id, schoolYearStart, thresholdAtRaise: 10));
        dbContext.StudentAlerts.Add(
            AlertFixture.NewOpenAlert(student.Id, school.Id, schoolYearStart, thresholdAtRaise: 10));

        ConcurrencyConflictException exception = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => dbContext.SaveChangesAsync(CancellationToken.None));

        Assert.Equal(ErrorCodes.Alert.DuplicateOpenEpisode, exception.ErrorCode);
        Assert.Equal("ix_student_alerts_open_episode", exception.ConstraintName);
    }

    /// <summary>
    ///     <c>ck_student_alerts_resolution_consistent</c> is satisfied by construction: all four
    ///     resolution fields are written in one statement. This is the assertion that the write
    ///     actually reached the row rather than only the tracked instance.
    /// </summary>
    [Fact]
    public async Task Resolve_WritesAllFourResolutionColumns()
    {
        const int schoolYearStart = 2083;

        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();
        School school = await AlertFixture.SchoolAsync(dbContext, threshold: 10);
        Student student = await AlertFixture.StudentAsync(dbContext, school.Id);
        StudentAlert alert = await AlertFixture.OpenAlertAsync(
            dbContext, student.Id, school.Id, schoolYearStart);

        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(school.Id);
        FakeTimeProvider clock = new(ContainerDbContextFactory.DefaultNow);
        clock.Advance(TimeSpan.FromHours(4));

        ResolveAlert.CommandHandler handler = new(
            dbContext, caller, clock, NullLogger<ResolveAlert.CommandHandler>.Instance);

        await handler.Handle(
            new ResolveAlert.Command { AlertId = alert.Id, Reason = Reason },
            CancellationToken.None);

        await using SparkrockRwcDbContext reader = fixture.CreateDbContext();

        StudentAlert persisted = await reader.StudentAlerts
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == alert.Id, CancellationToken.None);

        Assert.Equal(clock.GetUtcNow(), persisted.ResolvedAt);
        Assert.Equal(caller.UserId, persisted.ResolvedBy);
        Assert.Equal(ResolutionSource.Manual, persisted.ResolutionSource);
        Assert.Equal(Reason, persisted.ResolutionReason);
    }
}
