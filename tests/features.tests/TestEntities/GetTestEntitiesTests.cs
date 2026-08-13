using domain;
using domain.Exceptions;
using features.Paging;
using features.TestEntities;
using FluentValidation.Results;
using infra.persistence.postgre;
using Microsoft.Extensions.Time.Testing;

namespace features.tests.TestEntities;

public sealed class GetTestEntitiesHandlerTests
{
    [Fact]
    public async Task Handle_WhenNoEntitiesExist_ReturnsEmptyList()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        GetTestEntities.QueryHandler handler = new(dbContext);

        PagedResponse<GetTestEntities.Response> result = await handler.Handle(new GetTestEntities.Query(), CancellationToken.None);

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Handle_ReturnsPagedEnvelope()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        for (int index = 0; index < 3; index++)
        {
            dbContext.TestEntities.Add(new TestEntity { Id = Guid.NewGuid(), TestProperty = $"entity-{index}" });
        }

        await dbContext.SaveChangesAsync(CancellationToken.None);

        GetTestEntities.QueryHandler handler = new(dbContext);
        PagedResponse<GetTestEntities.Response> result = await handler.Handle(
            new GetTestEntities.Query { Page = 2, PageSize = 2 },
            CancellationToken.None);

        Assert.Single(result.Items);
        Assert.Equal(2, result.Page.Number);
        Assert.Equal(2, result.Page.Size);
        Assert.Equal(3, result.Page.TotalItems);
        Assert.Equal(2, result.Page.TotalPages);
    }

    [Fact]
    public async Task Handle_WhenPagingIsAbsent_UsesTheDefaultPage()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        dbContext.TestEntities.Add(new TestEntity { Id = Guid.NewGuid(), TestProperty = "only" });
        await dbContext.SaveChangesAsync(CancellationToken.None);

        GetTestEntities.QueryHandler handler = new(dbContext);
        PagedResponse<GetTestEntities.Response> result = await handler.Handle(new GetTestEntities.Query(), CancellationToken.None);

        Assert.Equal(PagingRules.DefaultPage, result.Page.Number);
        Assert.Equal(PagingRules.DefaultPageSize, result.Page.Size);
        Assert.Equal(1, result.Page.TotalItems);
        Assert.Equal(1, result.Page.TotalPages);
    }

    /// <summary>
    ///     CreatedAt is now stamped by the interceptor, so the test controls the clock rather than the
    ///     value. Hand-setting it is not merely discouraged — the setter is gone.
    /// </summary>
    [Fact]
    public async Task Handle_ProjectsIdAndPropertyAndCreatedAt()
    {
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();
        DateTimeOffset createdAt = clock.GetUtcNow();
        Guid id = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock);
        dbContext.TestEntities.Add(new TestEntity { Id = id, TestProperty = "projected" });
        await dbContext.SaveChangesAsync(CancellationToken.None);

        GetTestEntities.QueryHandler handler = new(dbContext);
        PagedResponse<GetTestEntities.Response> result = await handler.Handle(new GetTestEntities.Query(), CancellationToken.None);

        GetTestEntities.Response response = Assert.Single(result.Items);
        Assert.Equal(id, response.Id);
        Assert.Equal("projected", response.TestProperty);
        Assert.Equal(createdAt, response.CreatedAt);
    }

    /// <summary>
    ///     Rows are ordered by an interceptor-stamped column, so the clock is what separates them.
    ///     One save per row with the clock advanced between is the only way to make the order real.
    /// </summary>
    [Fact]
    public async Task Handle_OrdersByCreatedAtDescending()
    {
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock);

        foreach (string name in new[] { "oldest", "middle", "newest" })
        {
            dbContext.TestEntities.Add(new TestEntity { Id = Guid.NewGuid(), TestProperty = name });
            await dbContext.SaveChangesAsync(CancellationToken.None);
            clock.Advance(TimeSpan.FromHours(1));
        }

        GetTestEntities.QueryHandler handler = new(dbContext);
        PagedResponse<GetTestEntities.Response> result = await handler.Handle(new GetTestEntities.Query(), CancellationToken.None);

        Assert.Equal(["newest", "middle", "oldest"], result.Items.Select(response => response.TestProperty));
    }

    /// <summary>
    ///     A soft-deleted row is created by removing it, not by setting a flag. The interceptor
    ///     rewrites the delete, so this exercises the real path rather than simulating its outcome.
    /// </summary>
    [Fact]
    public async Task Handle_ExcludesSoftDeletedEntities()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        TestEntity visible = new() { Id = Guid.NewGuid(), TestProperty = "visible" };
        TestEntity removed = new() { Id = Guid.NewGuid(), TestProperty = "deleted" };
        dbContext.TestEntities.AddRange(visible, removed);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        dbContext.TestEntities.Remove(removed);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        GetTestEntities.QueryHandler handler = new(dbContext);
        PagedResponse<GetTestEntities.Response> result = await handler.Handle(new GetTestEntities.Query(), CancellationToken.None);

        Assert.Equal("visible", Assert.Single(result.Items).TestProperty);
        Assert.True(removed.IsDeleted);
        Assert.NotNull(removed.DeletedAt);
    }
}

/// <summary>
///     The paging tier of the reference slice. Named for <c>Handle</c> because tasks.md T01a-12 fixes
///     the name; the assertion is on the validator, which is where the failure is produced.
/// </summary>
public sealed class GetTestEntitiesValidatorTests
{
    private readonly GetTestEntities.QueryValidator _validator = new();

    [Fact]
    public void Handle_WhenPageSizeExceedsMax_Fails()
    {
        GetTestEntities.Query query = new() { PageSize = PagingRules.MaxPageSize + 1 };

        ValidationResult result = _validator.Validate(query);

        Assert.False(result.IsValid);
        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(GetTestEntities.Query.PageSize), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.PageSizeExceeded, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenPagingIsAbsent_Succeeds()
    {
        ValidationResult result = _validator.Validate(new GetTestEntities.Query());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }
}
