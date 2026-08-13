using domain;
using features.integration.tests.Fakes;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace features.integration.tests.Persistence;

/// <summary>
///     Proves the harness end to end against the only entity the model currently has.
/// </summary>
/// <remarks>
///     Every assertion here is one EF InMemory structurally cannot make: it has no DDL, no migration
///     history, no unique constraints, no <c>SqlState</c>, and no way to distinguish a filtered row
///     from an absent one. That is the conventions §6 tier rule applied — nothing below is also
///     asserted in <c>features.tests</c>.
///     <para>
///         <b>What is deliberately missing.</b> <c>TestEntity</c> carries no filtered unique index —
///         the <c>Init</c> migration creates a primary key and nothing else — so the strongest
///         available proof of the provider-error path is the primary key's own <c>23505</c>. VC-09's
///         "<c>HasFilter</c> is not rewritten by the naming convention" and DEC-14's <c>xmin</c>
///         concurrency token are both verified in F01d, which is where the entities carrying them
///         arrive. See the F01f spec's conflict note.
///     </para>
///     <para>
///         The database is shared across the collection: every test allocates its own
///         <see cref="Guid" /> and asserts only about its own rows.
///     </para>
/// </remarks>
[Collection(IntegrationTestCollectionDefinition.Name)]
public sealed class TestEntityPersistenceTests(PostgresContainerFixture fixture)
{
    [Fact]
    public async Task Migrate_CreatesTestEntitiesTableWithSnakeCaseColumns()
    {
        IReadOnlyList<string> columns = await DatabaseProbe.StringsAsync(
            fixture.ConnectionString,
            """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'test_entities'
            ORDER BY column_name
            """);

        // The column set the Init migration declares, snake_cased. If UseSnakeCaseNamingConvention
        // ever drifts between the runtime registration and the design-time factory, the table exists
        // under one casing and is queried under the other — this is where that shows up.
        Assert.Equal(
            [
                "created_at", "created_by", "deleted_at", "deleted_by",
                "id", "is_deleted", "modified_at", "modified_by", "test_property"
            ],
            columns);
    }

    [Fact]
    public async Task Migrate_RecordsInitInTheMigrationsHistory()
    {
        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();

        IEnumerable<string> applied = await dbContext.Database.GetAppliedMigrationsAsync();

        // Proves the fixture migrated rather than silently no-oping onto a database someone else
        // created. Asserted through EF's own history reader so the history table's name and column
        // casing stay EF's business, not this test's.
        Assert.Contains("20260813102015_Init", applied);
    }

    [Fact]
    public async Task SaveChangesAsync_WhenEntityRemoved_LeavesRowPhysicallyPresent()
    {
        FakeCurrentUser actor = new();
        Guid id;

        await using (SparkrockRwcDbContext dbContext = fixture.CreateDbContext(currentUser: actor))
        {
            TestEntity created = new() { TestProperty = "soft-delete round trip" };
            dbContext.TestEntities.Add(created);
            await dbContext.SaveChangesAsync();
            id = created.Id;
        }

        await using (SparkrockRwcDbContext dbContext = fixture.CreateDbContext(currentUser: actor))
        {
            TestEntity loaded = await dbContext.TestEntities.SingleAsync(e => e.Id == id);
            dbContext.TestEntities.Remove(loaded);
            await dbContext.SaveChangesAsync();
        }

        await using (SparkrockRwcDbContext dbContext = fixture.CreateDbContext())
        {
            Assert.Null(await dbContext.TestEntities.FirstOrDefaultAsync(e => e.Id == id));
        }

        // The assertion the in-memory tier cannot make: the DELETE became an UPDATE, so the row is
        // still in the table. A filtered EF read returning nothing is equally consistent with the row
        // having been physically deleted, which is exactly the failure DEC-20's guard exists to catch.
        bool isDeleted = await DatabaseProbe.ScalarAsync<bool>(
            fixture.ConnectionString,
            "SELECT is_deleted FROM test_entities WHERE id = @id",
            new NpgsqlParameter("id", id));

        Guid deletedBy = await DatabaseProbe.ScalarAsync<Guid>(
            fixture.ConnectionString,
            "SELECT deleted_by FROM test_entities WHERE id = @id",
            new NpgsqlParameter("id", id));

        bool deletedAtStamped = await DatabaseProbe.ScalarAsync<bool>(
            fixture.ConnectionString,
            "SELECT deleted_at IS NOT NULL FROM test_entities WHERE id = @id",
            new NpgsqlParameter("id", id));

        Guid createdBy = await DatabaseProbe.ScalarAsync<Guid>(
            fixture.ConnectionString,
            "SELECT created_by FROM test_entities WHERE id = @id",
            new NpgsqlParameter("id", id));

        Assert.True(isDeleted);
        Assert.True(deletedAtStamped);
        Assert.Equal(actor.UserId, deletedBy);
        Assert.Equal(actor.UserId, createdBy);
    }

    [Fact]
    public async Task SaveChangesAsync_WhenDuplicateKeyInserted_ThrowsPostgresExceptionWithConstraintName()
    {
        Guid id = Guid.NewGuid();

        await using SparkrockRwcDbContext first = fixture.CreateDbContext();
        first.TestEntities.Add(new TestEntity { Id = id, TestProperty = "original" });
        await first.SaveChangesAsync();

        // A second context, so the duplicate is caught by the database rather than by the change
        // tracker's identity map.
        await using SparkrockRwcDbContext second = fixture.CreateDbContext();
        second.TestEntities.Add(new TestEntity { Id = id, TestProperty = "duplicate" });

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());

        // VC-23: the SqlState and the constraint name are reachable through the inner exception, which
        // is the entire basis of the conventions §5 constraint-to-error-code table. F03 is the first
        // feature to depend on it (ix_attendance_codes_value), and it depends on this shape holding.
        PostgresException inner = Assert.IsType<PostgresException>(exception.InnerException);

        Assert.Equal("23505", inner.SqlState);
        Assert.Equal("pk_test_entities", inner.ConstraintName);
    }

    [Fact]
    public void Create_DefaultsToNonAdminIdentity()
    {
        FakeCurrentUser identity = new();

        // Guards the factory default at the point it would be regressed. The production stub is a
        // system administrator; a double copying that would let a handler that omitted its
        // authorisation scoping pass against a real database, which happily returns the unscoped rows.
        Assert.False(identity.IsSystemAdmin);
        Assert.Empty(identity.AuthorizedSchoolIds);
    }
}
