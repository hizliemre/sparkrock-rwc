using domain;
using domain.Security;
using features.tests.Fakes;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace features.tests.Persistence;

public sealed class AuditableEntityInterceptorTests
{
    private static readonly Guid Actor = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task SaveChanges_WhenAdded_StampsCreatedFromTheCurrentUserAndClock()
    {
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();
        DateTimeOffset now = clock.GetUtcNow();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(
            clock, new FakeCurrentUser { UserId = Actor });

        TestEntity entity = new() { Id = Guid.NewGuid(), TestProperty = "created" };
        dbContext.TestEntities.Add(entity);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        Assert.Equal(Actor, entity.CreatedBy);
        Assert.Equal(now, entity.CreatedAt);
        Assert.Null(entity.ModifiedAt);
        Assert.Null(entity.ModifiedBy);
    }

    [Fact]
    public async Task SaveChanges_WhenModified_StampsModifiedAndLeavesCreatedAlone()
    {
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();
        DateTimeOffset createdAt = clock.GetUtcNow();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(
            clock, new FakeCurrentUser { UserId = Actor });

        TestEntity entity = new() { Id = Guid.NewGuid(), TestProperty = "before" };
        dbContext.TestEntities.Add(entity);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(5));
        entity.TestProperty = "after";
        await dbContext.SaveChangesAsync(CancellationToken.None);

        Assert.Equal(createdAt, entity.CreatedAt);
        Assert.Equal(clock.GetUtcNow(), entity.ModifiedAt);
        Assert.Equal(Actor, entity.ModifiedBy);
    }

    [Fact]
    public async Task SaveChanges_WhenRemoved_RewritesToASoftDelete()
    {
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(
            clock, new FakeCurrentUser { UserId = Actor });

        TestEntity entity = new() { Id = Guid.NewGuid(), TestProperty = "doomed" };
        dbContext.TestEntities.Add(entity);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        dbContext.TestEntities.Remove(entity);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        Assert.True(entity.IsDeleted);
        Assert.Equal(clock.GetUtcNow(), entity.DeletedAt);
        Assert.Equal(Actor, entity.DeletedBy);
        Assert.Equal(EntityState.Unchanged, dbContext.Entry(entity).State);
    }

    /// <summary>
    ///     The delete-by-id pattern: remove a stub without loading the row first.
    /// </summary>
    /// <remarks>
    ///     The rewrite used to set <c>State = Modified</c>, which marks every property modified — so
    ///     EF wrote the stub's defaults over the real row. It came back with an empty name, a zero
    ///     <c>CreatedBy</c> and a year-0001 <c>CreatedAt</c>, silently, because all of those are
    ///     legal values. Only the five columns the rewrite owns may be written.
    /// </remarks>
    [Fact]
    public async Task SaveChanges_WhenRemovingADetachedStub_LeavesEveryOtherColumnIntact()
    {
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();
        DateTimeOffset createdAt = clock.GetUtcNow();
        Guid id = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(
            clock, new FakeCurrentUser { UserId = Actor });

        dbContext.TestEntities.Add(new TestEntity { Id = id, TestProperty = "real value" });
        await dbContext.SaveChangesAsync(CancellationToken.None);
        dbContext.ChangeTracker.Clear();

        dbContext.TestEntities.Remove(new TestEntity { Id = id, TestProperty = null! });
        await dbContext.SaveChangesAsync(CancellationToken.None);
        dbContext.ChangeTracker.Clear();

        TestEntity stored = await dbContext.TestEntities
            .IgnoreQueryFilters()
            .SingleAsync(e => e.Id == id, CancellationToken.None);

        Assert.True(stored.IsDeleted);
        Assert.Equal("real value", stored.TestProperty);
        Assert.Equal(createdAt, stored.CreatedAt);
        Assert.Equal(Actor, stored.CreatedBy);
    }

    [Fact]
    public async Task SaveChanges_WhenOverrideIsActive_AttributesToTheOverrideActor()
    {
        AuditOverride auditOverride = new();
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(
            currentUser: new FakeCurrentUser { UserId = Actor }, auditOverride: auditOverride);

        TestEntity entity = new() { Id = Guid.NewGuid(), TestProperty = "imported" };

        using (auditOverride.Begin(SystemImportUser.Id))
        {
            dbContext.TestEntities.Add(entity);
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }

        Assert.Equal(SystemImportUser.Id, entity.CreatedBy);
        Assert.NotEqual(Actor, entity.CreatedBy);
    }
}
