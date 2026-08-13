using domain.AttendanceCodes;
using domain.Schools;
using domain.Security;
using domain.Students;
using domain.ValueObjects;
using features.tests.Fakes;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using tools.seed;

namespace features.tests.Seed;

/// <summary>
///     <see cref="SeedWriter" /> against the real model on the in-memory provider.
/// </summary>
/// <remarks>
///     This tier covers the writer's <em>logic</em>: which rows it creates, which it copies onto,
///     which it refuses to touch, and whose identity it stamps. It does <b>not</b> cover the
///     relational half — the unfiltered unique index on <c>attendance_codes.value</c>, the uppercase
///     check constraint, and whether a second run issues any statement at all — because EF InMemory
///     enforces none of it. Those live in
///     <c>tests/features.integration.tests/Seed/SeedWriterPostgresTests.cs</c>.
/// </remarks>
public sealed class SeedWriterTests
{
    private static readonly SchoolYear Year = SchoolYear.FromStartYear(2026);

    private static readonly int TotalRows = 1 + 5 + 4 + 32;

    [Fact]
    public async Task WriteAsync_WhenDatabaseIsEmpty_CreatesEveryRow()
    {
        SeedInMemoryDatabase database = new();

        SeedResult result = await SeedAsync(database);

        Assert.Equal(TotalRows, result.TotalCreated);
        Assert.Equal(0, result.TotalUpdated);
        Assert.Equal(0, result.TotalSkipped);
        Assert.Empty(result.Conflicts);

        await using SparkrockRwcDbContext dbContext = database.Connect();

        Assert.Equal(1, await dbContext.Schools.CountAsync());
        Assert.Equal(5, await dbContext.AttendanceCodes.CountAsync());
        Assert.Equal(4, await dbContext.SchoolTerms.CountAsync());
        Assert.Equal(32, await dbContext.Students.CountAsync());
    }

    /// <summary>
    ///     Running twice leaves the database exactly as running once did.
    /// </summary>
    /// <remarks>
    ///     The second run goes through a <b>new context against the populated database</b>, so nothing
    ///     from the first run is still tracked. Both halves are asserted: no second copy of any row,
    ///     and no write to any existing one — the latter via <c>ModifiedAt</c>, which the interceptor
    ///     stamps on every update and which the clock advance between runs would make visible.
    /// </remarks>
    [Fact]
    public async Task WriteAsync_WhenRunTwice_CreatesNothingTheSecondTime()
    {
        SeedInMemoryDatabase database = new();

        await SeedAsync(database);
        database.Clock.Advance(TimeSpan.FromDays(1));
        SeedResult second = await SeedAsync(database);

        Assert.Equal(0, second.TotalCreated);
        Assert.Equal(0, second.TotalUpdated);
        Assert.Equal(TotalRows, second.TotalUnchanged);

        await using SparkrockRwcDbContext dbContext = database.Connect();

        Assert.Equal(1, await dbContext.Schools.CountAsync());
        Assert.Equal(5, await dbContext.AttendanceCodes.CountAsync());
        Assert.Equal(4, await dbContext.SchoolTerms.CountAsync());
        Assert.Equal(32, await dbContext.Students.CountAsync());

        // Every kind, not a sample of them. An earlier version of this test asserted on students and
        // the school only, and a mutation that rewrote attendance_codes on every run passed it.
        DateTimeOffset?[] stamps =
        [
            .. (await dbContext.Schools.ToListAsync()).Select(row => row.ModifiedAt),
            .. (await dbContext.AttendanceCodes.ToListAsync()).Select(row => row.ModifiedAt),
            .. (await dbContext.SchoolTerms.ToListAsync()).Select(row => row.ModifiedAt),
            .. (await dbContext.Students.ToListAsync()).Select(row => row.ModifiedAt)
        ];

        Assert.Equal(TotalRows, stamps.Length);
        Assert.All(stamps, Assert.Null);
    }

    /// <summary>The seed is an upsert, not an insert-if-absent.</summary>
    [Fact]
    public async Task WriteAsync_WhenARowWasEdited_RestoresTheSeededValues()
    {
        SeedInMemoryDatabase database = new();

        await SeedAsync(database);

        await using (SparkrockRwcDbContext arrange = database.Connect())
        {
            School school = await arrange.Schools.SingleAsync();
            school.Name = "Renamed";
            school.IsActive = false;
            await arrange.SaveChangesAsync(CancellationToken.None);
        }

        SeedResult result = await SeedAsync(database);

        Assert.Equal(1, result.TotalUpdated);

        await using SparkrockRwcDbContext dbContext = database.Connect();
        School restored = await dbContext.Schools.SingleAsync();

        Assert.Equal(SeedCatalog.SchoolName, restored.Name);
        Assert.True(restored.IsActive);
    }

    /// <summary>
    ///     Nothing is ever removed.
    /// </summary>
    /// <remarks>
    ///     <c>Remove</c> on a <c>BaseEntity</c> throws in the interceptor (DEC-20), and that guard must
    ///     not be worked around: a seed that deleted "rows it did not recognise" would delete a
    ///     developer's hand-made test data along with its own.
    /// </remarks>
    [Fact]
    public async Task WriteAsync_WhenAnUnrelatedRowWasAdded_LeavesItAlone()
    {
        SeedInMemoryDatabase database = new();
        Guid strangerId = Guid.NewGuid();

        await SeedAsync(database);

        await using (SparkrockRwcDbContext arrange = database.Connect())
        {
            arrange.Students.Add(new Student
            {
                Id = strangerId,
                SchoolId = SeedIds.School,
                FirstName = "Hand",
                LastName = "Made",
                IsActive = true
            });

            arrange.AttendanceCodes.Add(new AttendanceCode
            {
                Id = Guid.NewGuid(),
                Value = "Z",
                Description = "Someone else's code",
                IsActive = true
            });

            await arrange.SaveChangesAsync(CancellationToken.None);
        }

        await SeedAsync(database);

        await using SparkrockRwcDbContext dbContext = database.Connect();

        Assert.Equal(33, await dbContext.Students.CountAsync());
        Assert.Equal(6, await dbContext.AttendanceCodes.CountAsync());
        Assert.NotNull(await dbContext.Students.FirstOrDefaultAsync(student => student.Id == strangerId));
    }

    /// <summary>
    ///     O-30's branch: a foreign row already holds one of the seeded code values.
    /// </summary>
    /// <remarks>
    ///     Asserted here as <em>logic</em> — the row is skipped and the skip is reported, so the
    ///     operator finds out. The consequence that needs a real database, that the run does not abort
    ///     on a <c>23505</c> from the unfiltered unique index, is asserted in the integration tier;
    ///     the in-memory provider has no unique index and would have accepted a second <c>P</c>.
    /// </remarks>
    [Fact]
    public async Task WriteAsync_WhenAnotherRowHoldsASeededCodeValue_SkipsItAndReportsIt()
    {
        SeedInMemoryDatabase database = new();
        Guid foreignId = Guid.NewGuid();

        await using (SparkrockRwcDbContext arrange = database.Connect())
        {
            arrange.AttendanceCodes.Add(new AttendanceCode
            {
                Id = foreignId,
                Value = "P",
                Description = "Created through F03",
                IsActive = true
            });

            await arrange.SaveChangesAsync(CancellationToken.None);
        }

        SeedResult result = await SeedAsync(database);

        Assert.Equal(1, result.TotalSkipped);
        Assert.Single(result.Conflicts);
        Assert.Contains(foreignId.ToString(), result.Conflicts[0], StringComparison.Ordinal);

        await using SparkrockRwcDbContext dbContext = database.Connect();

        Assert.Null(await dbContext.AttendanceCodes
            .FirstOrDefaultAsync(code => code.Id == SeedIds.AttendanceCodes[0]));
        Assert.Equal("Created through F03",
            (await dbContext.AttendanceCodes.SingleAsync(code => code.Id == foreignId)).Description);
    }

    /// <summary>
    ///     <c>LegacyId</c> is never written, so a re-run cannot un-adopt a row F12 has claimed.
    /// </summary>
    /// <remarks>
    ///     This is the half of the O-30 contract F00 owes F12. The importer's match key for
    ///     <c>AttendanceCode</c> is <c>LegacyId</c> first and <c>UPPER(Value)</c> second, and on a
    ///     <c>Value</c> match it adopts the seeded row by writing <c>LegacyId</c> onto it. If the next
    ///     seed run nulled that back out, the import after it would find no match and insert a
    ///     duplicate — into an unfiltered unique index, so it would fail, and it would fail as a wall
    ///     of anomaly rows that reads like bad legacy data rather than a design collision.
    /// </remarks>
    [Fact]
    public async Task WriteAsync_WhenASeededRowHasBeenAdopted_LeavesItsLegacyIdAlone()
    {
        SeedInMemoryDatabase database = new();

        await SeedAsync(database);

        await using (SparkrockRwcDbContext adopt = database.Connect())
        {
            AttendanceCode code = await adopt.AttendanceCodes
                .SingleAsync(row => row.Id == SeedIds.AttendanceCodes[0]);

            code.LegacyId = 7;
            code.Description = "Present (legacy wording)";
            await adopt.SaveChangesAsync(CancellationToken.None);
        }

        await SeedAsync(database);

        await using SparkrockRwcDbContext dbContext = database.Connect();
        AttendanceCode adopted = await dbContext.AttendanceCodes
            .SingleAsync(row => row.Id == SeedIds.AttendanceCodes[0]);

        Assert.Equal(7, adopted.LegacyId);
        // The descriptive columns are the seed's, and it does take those back.
        Assert.Equal("Present", adopted.Description);
    }

    /// <summary>
    ///     Seed rows carry the reserved import identity whoever the ambient identity is.
    /// </summary>
    /// <remarks>
    ///     Built with an ordinary <see cref="FakeCurrentUser" /> rather than
    ///     <c>SystemImportUser.AsCurrentUser()</c> on purpose. With the import identity registered,
    ///     the interceptor's override branch and its fall-through branch produce the same answer, so
    ///     the assertion would pass whether or not <see cref="SeedWriter" /> opened an audit override
    ///     at all — which is a test that cannot tell the mechanism from its absence.
    /// </remarks>
    [Fact]
    public async Task WriteAsync_StampsTheImportIdentityRatherThanTheAmbientOne()
    {
        SeedInMemoryDatabase database = new();
        FakeCurrentUser someoneElse = new();

        await SeedAsync(database, someoneElse);

        await using SparkrockRwcDbContext dbContext = database.Connect();
        School school = await dbContext.Schools.SingleAsync();

        Assert.Equal(SystemImportUser.Id, school.CreatedBy);
        Assert.NotEqual(someoneElse.UserId, school.CreatedBy);
        Assert.Equal(database.Clock.GetUtcNow(), school.CreatedAt);
    }

    /// <summary>An override that is already active is a programming error, not a nested scope.</summary>
    [Fact]
    public async Task WriteAsync_WhenAnOverrideIsAlreadyActive_Throws()
    {
        SeedInMemoryDatabase database = new();
        AuditOverride auditOverride = new();

        using IDisposable outer = auditOverride.Begin(Guid.NewGuid());

        await using SparkrockRwcDbContext dbContext = database.Connect(auditOverride: auditOverride);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SeedWriter(dbContext, auditOverride).WriteAsync(SeedCatalog.Build(Year), CancellationToken.None));
    }

    private static async Task<SeedResult> SeedAsync(SeedInMemoryDatabase database, ICurrentUser? currentUser = null)
    {
        AuditOverride auditOverride = new();

        await using SparkrockRwcDbContext dbContext = database.Connect(currentUser, auditOverride);

        return await new SeedWriter(dbContext, auditOverride)
            .WriteAsync(SeedCatalog.Build(Year), CancellationToken.None);
    }
}
