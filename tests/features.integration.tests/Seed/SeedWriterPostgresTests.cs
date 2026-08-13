using domain.AttendanceCodes;
using domain.Schools;
using domain.Security;
using domain.SchoolTerms;
using domain.Students;
using domain.ValueObjects;
using features.integration.tests.Fakes;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using tools.seed;

namespace features.integration.tests.Seed;

/// <summary>
///     F00's seeder against real PostgreSQL.
/// </summary>
/// <remarks>
///     F00's plan said there was no integration tier here, on the grounds that nothing in the seed
///     depends on relational behaviour. That is not right, and the assertions below are the argument:
///     idempotency is a statement about what a second <c>INSERT</c> does against a populated table
///     with a primary key; the O-30 collision is a statement about an <em>unfiltered</em> unique
///     index; the uppercase rule is a <c>CHECK</c> constraint. EF InMemory enforces none of the
///     three — it has no unique index, no check constraint and no <c>23505</c> — so a seed whose
///     idempotency was proved only in memory is a seed whose idempotency was not proved.
///     <para>
///         Each test runs against its own database on the shared container. See
///         <see cref="SeedDatabase" /> for why this tier's usual shared-database convention cannot
///         apply to fixed primary keys and a globally unique code value.
///     </para>
/// </remarks>
[Collection(IntegrationTestCollectionDefinition.Name)]
public sealed class SeedWriterPostgresTests(PostgresContainerFixture fixture)
{
    private static readonly SchoolYear SchoolYear = SchoolYear.FromStartYear(2026);

    private static readonly DateTimeOffset FirstRun = new(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);

    /// <summary>The query the cutover runbook runs before an import. It must mean something.</summary>
    private const string PreconditionQuery =
        "SELECT count(*) FROM attendance_codes WHERE id::text LIKE 'f0%'";

    [Fact]
    public async Task WriteAsync_WhenDatabaseIsEmpty_CreatesEveryRowUnderTheReservedIdPrefix()
    {
        string connectionString = await SeedDatabase.CreateAsync(fixture.ConnectionString, "empty");

        SeedResult result = await SeedAsync(connectionString);

        Assert.Equal(1 + 5 + 4 + 32, result.TotalCreated);
        Assert.Empty(result.Conflicts);

        await using SparkrockRwcDbContext dbContext = Context(connectionString);

        Assert.Equal(1, await dbContext.Schools.CountAsync());
        Assert.Equal(5, await dbContext.AttendanceCodes.CountAsync());
        Assert.Equal(4, await dbContext.SchoolTerms.CountAsync());
        Assert.Equal(32, await dbContext.Students.CountAsync());

        // The runbook's own SQL, not a paraphrase of it. uuid::text renders lower case in Postgres,
        // so a prefix written in upper case would match nothing and the precondition would report a
        // clean database no matter what was in it.
        Assert.Equal(5, await SeedDatabase.ScalarAsync(connectionString, PreconditionQuery));

        // Every seeded table, not the two that happened to be typed out first. An earlier version of
        // this checked attendance_codes and students only, and a school id that escaped the prefix
        // passed it — which is exactly the under-report the prefix scheme exists to prevent, in the
        // exact query the cutover runbook will trust.
        foreach (string table in (string[])["schools", "attendance_codes", "school_terms", "students"])
        {
            Assert.Equal(0, await SeedDatabase.ScalarAsync(
                connectionString,
                $"SELECT count(*) FROM {table} WHERE id::text NOT LIKE 'f0%'"));
        }
    }

    /// <summary>
    ///     The property this feature is most likely to lose and least likely to notice losing.
    /// </summary>
    /// <remarks>
    ///     The second run happens against the <b>populated</b> database, through a fresh context with
    ///     an empty change tracker, so nothing about the first run is still in memory to make the
    ///     second one look like a no-op.
    ///     <para>
    ///         <c>modified_at</c> is the assertion that does not depend on the writer's own bookkeeping.
    ///         The interceptor stamps it on every <c>UPDATE</c>, and the clock is advanced by a day
    ///         between runs, so any statement the second run issued against a seeded row leaves a
    ///         mark the writer cannot suppress. Counting rows catches a duplicate insert; this catches
    ///         a pointless write.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task WriteAsync_WhenRunTwiceAgainstAPopulatedDatabase_WritesNothingTheSecondTime()
    {
        string connectionString = await SeedDatabase.CreateAsync(fixture.ConnectionString, "twice");

        await SeedAsync(connectionString);
        SeedResult second = await SeedAsync(connectionString, FirstRun.AddDays(1));

        Assert.Equal(0, second.TotalCreated);
        Assert.Equal(0, second.TotalUpdated);
        Assert.Equal(1 + 5 + 4 + 32, second.TotalUnchanged);
        Assert.Empty(second.Conflicts);

        await using SparkrockRwcDbContext dbContext = Context(connectionString);

        Assert.Equal(1, await dbContext.Schools.CountAsync());
        Assert.Equal(5, await dbContext.AttendanceCodes.CountAsync());
        Assert.Equal(4, await dbContext.SchoolTerms.CountAsync());
        Assert.Equal(32, await dbContext.Students.CountAsync());

        Assert.Equal(0, await SeedDatabase.ScalarAsync(
            connectionString,
            "SELECT count(*) FROM (SELECT modified_at FROM schools UNION ALL "
            + "SELECT modified_at FROM attendance_codes UNION ALL "
            + "SELECT modified_at FROM school_terms UNION ALL "
            + "SELECT modified_at FROM students) rows WHERE modified_at IS NOT NULL"));
    }

    /// <summary>
    ///     O-30's collision, reproduced. This is the one that cannot be written at the handler tier.
    /// </summary>
    /// <remarks>
    ///     <c>ix_attendance_codes_value</c> is unique and unfiltered, so a row holding <c>P</c> under
    ///     any other id makes the seeded <c>P</c> unwritable. A writer that inserted anyway would take
    ///     a <c>23505</c> that rolls back the single <c>SaveChangesAsync</c> and leaves nothing seeded
    ///     at all — the failure is not "one code missing", it is "the whole run aborted". EF InMemory
    ///     has no unique index, so it would have accepted the second row and this would have looked
    ///     fine.
    /// </remarks>
    [Fact]
    public async Task WriteAsync_WhenAnotherRowHoldsASeededCodeValue_LeavesItAloneAndSeedsTheRest()
    {
        string connectionString = await SeedDatabase.CreateAsync(fixture.ConnectionString, "collision");
        Guid foreignId = Guid.NewGuid();

        await using (SparkrockRwcDbContext arrange = Context(connectionString))
        {
            arrange.AttendanceCodes.Add(new AttendanceCode
            {
                Id = foreignId,
                Value = "P",
                Description = "Present, created through F03",
                IsAbsent = false,
                IsExcused = false,
                IsActive = true,
                LegacyId = 4242
            });

            await arrange.SaveChangesAsync(CancellationToken.None);
        }

        SeedResult result = await SeedAsync(connectionString);

        Assert.Single(result.Conflicts);
        Assert.Contains("'P'", result.Conflicts[0], StringComparison.Ordinal);

        await using SparkrockRwcDbContext dbContext = Context(connectionString);

        // Four of five seeded, plus the pre-existing row. Nothing aborted.
        Assert.Equal(5, await dbContext.AttendanceCodes.CountAsync());
        Assert.Null(await dbContext.AttendanceCodes.FirstOrDefaultAsync(code => code.Id == SeedIds.AttendanceCodes[0]));

        // Untouched, LegacyId included. A seed that "reconciled" this row would un-adopt whatever
        // F12 had already matched to it.
        AttendanceCode survivor = await dbContext.AttendanceCodes.SingleAsync(code => code.Id == foreignId);
        Assert.Equal("Present, created through F03", survivor.Description);
        Assert.Equal(4242, survivor.LegacyId);
        Assert.Null(survivor.ModifiedAt);
    }

    /// <summary>
    ///     The seed is an upsert, not an insert-if-absent.
    /// </summary>
    [Fact]
    public async Task WriteAsync_WhenARowWasEditedByHand_RestoresTheSeededValues()
    {
        string connectionString = await SeedDatabase.CreateAsync(fixture.ConnectionString, "edited");

        await SeedAsync(connectionString);

        await using (SparkrockRwcDbContext arrange = Context(connectionString))
        {
            School school = await arrange.Schools.SingleAsync(row => row.Id == SeedIds.School);
            school.Name = "Renamed by a developer";
            await arrange.SaveChangesAsync(CancellationToken.None);
        }

        SeedResult result = await SeedAsync(connectionString, FirstRun.AddDays(1));

        Assert.Equal(1, result.TotalUpdated);
        Assert.Equal(0, result.TotalCreated);

        await using SparkrockRwcDbContext dbContext = Context(connectionString);
        School restored = await dbContext.Schools.SingleAsync(row => row.Id == SeedIds.School);

        Assert.Equal(SeedCatalog.SchoolName, restored.Name);
        Assert.Equal(SystemImportUser.Id, restored.ModifiedBy);
    }

    /// <summary>
    ///     A row that is not the seed's is never removed, and never made to look like the seed's.
    /// </summary>
    [Fact]
    public async Task WriteAsync_WhenAnUnrelatedRowExists_LeavesIt()
    {
        string connectionString = await SeedDatabase.CreateAsync(fixture.ConnectionString, "unrelated");
        Guid strangerId = Guid.NewGuid();

        await SeedAsync(connectionString);

        await using (SparkrockRwcDbContext arrange = Context(connectionString))
        {
            arrange.Students.Add(new Student
            {
                Id = strangerId,
                SchoolId = SeedIds.School,
                FirstName = "Hand",
                LastName = "Made",
                Grade = "07",
                IsActive = true
            });

            await arrange.SaveChangesAsync(CancellationToken.None);
        }

        await SeedAsync(connectionString, FirstRun.AddDays(1));

        await using SparkrockRwcDbContext dbContext = Context(connectionString);

        Assert.Equal(33, await dbContext.Students.CountAsync());
        Student stranger = await dbContext.Students.SingleAsync(row => row.Id == strangerId);
        Assert.Equal("Made", stranger.LastName);
    }

    /// <summary>
    ///     Seed rows carry the reserved import identity, whoever the ambient identity happens to be.
    /// </summary>
    /// <remarks>
    ///     Constructed with an ordinary <see cref="FakeCurrentUser" /> rather than
    ///     <c>SystemImportUser.AsCurrentUser()</c> deliberately. With the import identity registered,
    ///     both the override branch and the fall-through branch of the interceptor produce the same
    ///     answer, so the assertion would hold whether or not <see cref="SeedWriter" /> opened an
    ///     audit override at all — a test that cannot distinguish the mechanism from its absence.
    /// </remarks>
    [Fact]
    public async Task WriteAsync_StampsTheImportIdentityRatherThanTheAmbientOne()
    {
        string connectionString = await SeedDatabase.CreateAsync(fixture.ConnectionString, "identity");
        FakeCurrentUser someoneElse = new();

        await SeedAsync(connectionString, currentUser: someoneElse);

        await using SparkrockRwcDbContext dbContext = Context(connectionString);

        School school = await dbContext.Schools.SingleAsync(row => row.Id == SeedIds.School);
        SchoolTerm term = await dbContext.SchoolTerms.FirstAsync();

        Assert.Equal(SystemImportUser.Id, school.CreatedBy);
        Assert.NotEqual(someoneElse.UserId, school.CreatedBy);
        Assert.Equal(SystemImportUser.Id, term.CreatedBy);
    }

    /// <summary>
    ///     The backstop the seed's normalisation exists to keep unreachable.
    /// </summary>
    /// <remarks>
    ///     F01c owns <c>ck_attendance_codes_value_upper</c>; this asserts it is actually enforced on
    ///     the deployed schema, because the whole argument for normalising in
    ///     <see cref="SeedCatalog" /> rests on the claim that a lowercase value would otherwise be
    ///     storable and would then collide case-sensitively with F12's import.
    /// </remarks>
    [Fact]
    public async Task ALowercaseAttendanceCodeValue_IsRejectedByTheCheckConstraint()
    {
        string connectionString = await SeedDatabase.CreateAsync(fixture.ConnectionString, "upper");

        await using SparkrockRwcDbContext dbContext = Context(connectionString);

        dbContext.AttendanceCodes.Add(new AttendanceCode
        {
            Id = Guid.NewGuid(),
            Value = "p",
            Description = "Lower case",
            IsAbsent = false,
            IsExcused = false,
            IsActive = true
        });

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync(CancellationToken.None));

        PostgresException postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal("23514", postgres.SqlState);
        Assert.Equal("ck_attendance_codes_value_upper", postgres.ConstraintName);
    }

    private static SparkrockRwcDbContext Context(
        string connectionString,
        DateTimeOffset? now = null,
        ICurrentUser? currentUser = null,
        IAuditOverride? auditOverride = null) =>
        ContainerDbContextFactory.Create(
            connectionString,
            new FakeTimeProvider(now ?? FirstRun),
            currentUser,
            auditOverride);

    /// <summary>One run of the seeder, through its own context, exactly as the console tool does.</summary>
    private static async Task<SeedResult> SeedAsync(
        string connectionString,
        DateTimeOffset? now = null,
        ICurrentUser? currentUser = null)
    {
        AuditOverride auditOverride = new();

        await using SparkrockRwcDbContext dbContext = Context(connectionString, now, currentUser, auditOverride);

        return await new SeedWriter(dbContext, auditOverride)
            .WriteAsync(SeedCatalog.Build(SchoolYear), CancellationToken.None);
    }
}
