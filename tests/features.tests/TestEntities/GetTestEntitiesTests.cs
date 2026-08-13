using domain;
using features.TestEntities;
using infra.persistence.postgre;

namespace features.tests.TestEntities;

public sealed class GetTestEntitiesHandlerTests
{
    [Fact]
    public async Task Handle_WhenNoEntitiesExist_ReturnsEmptyList()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        GetTestEntities.QueryHandler handler = new(dbContext);

        IReadOnlyList<GetTestEntities.Response> result = await handler.Handle(new GetTestEntities.Query(), CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Handle_ProjectsIdAndPropertyAndCreatedAt()
    {
        DateTimeOffset createdAt = new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
        Guid id = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        dbContext.TestEntities.Add(new TestEntity { Id = id, TestProperty = "projected", CreatedAt = createdAt });
        await dbContext.SaveChangesAsync(CancellationToken.None);

        GetTestEntities.QueryHandler handler = new(dbContext);
        IReadOnlyList<GetTestEntities.Response> result = await handler.Handle(new GetTestEntities.Query(), CancellationToken.None);

        GetTestEntities.Response response = Assert.Single(result);
        Assert.Equal(id, response.Id);
        Assert.Equal("projected", response.TestProperty);
        Assert.Equal(createdAt, response.CreatedAt);
    }

    [Fact]
    public async Task Handle_OrdersByCreatedAtDescending()
    {
        DateTimeOffset baseTime = new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        dbContext.TestEntities.AddRange(
            new TestEntity { Id = Guid.NewGuid(), TestProperty = "oldest", CreatedAt = baseTime },
            new TestEntity { Id = Guid.NewGuid(), TestProperty = "newest", CreatedAt = baseTime.AddHours(2) },
            new TestEntity { Id = Guid.NewGuid(), TestProperty = "middle", CreatedAt = baseTime.AddHours(1) });
        await dbContext.SaveChangesAsync(CancellationToken.None);

        GetTestEntities.QueryHandler handler = new(dbContext);
        IReadOnlyList<GetTestEntities.Response> result = await handler.Handle(new GetTestEntities.Query(), CancellationToken.None);

        Assert.Equal(["newest", "middle", "oldest"], result.Select(response => response.TestProperty));
    }

    [Fact]
    public async Task Handle_ExcludesSoftDeletedEntities()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        dbContext.TestEntities.AddRange(
            new TestEntity { Id = Guid.NewGuid(), TestProperty = "visible" },
            new TestEntity { Id = Guid.NewGuid(), TestProperty = "deleted", IsDeleted = true });
        await dbContext.SaveChangesAsync(CancellationToken.None);

        GetTestEntities.QueryHandler handler = new(dbContext);
        IReadOnlyList<GetTestEntities.Response> result = await handler.Handle(new GetTestEntities.Query(), CancellationToken.None);

        Assert.Equal("visible", Assert.Single(result).TestProperty);
    }
}
