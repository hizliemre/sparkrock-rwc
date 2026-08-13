using System.Globalization;
using domain;
using domain.Exceptions;
using features.Paging;
using FluentValidation;
using FluentValidation.Results;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;

namespace features.tests.Paging;

public sealed class PagingRulesTests
{
    private static readonly PagedRequestValidator Validator = new();

    [Fact]
    public void ResolvePage_WhenAbsent_ReturnsOne()
    {
        Assert.Equal(1, PagingRules.ResolvePage(null));
        Assert.Equal(1, PagingRules.DefaultPage);
    }

    [Fact]
    public void ResolvePageSize_WhenAbsent_ReturnsFifty()
    {
        Assert.Equal(50, PagingRules.ResolvePageSize(null));
        Assert.Equal(50, PagingRules.DefaultPageSize);
    }

    [Fact]
    public void ValidPageSize_WhenAboveMax_FailsWithPageSizeExceeded()
    {
        PagedRequest request = new() { PageSize = PagingRules.MaxPageSize + 1 };

        ValidationResult result = Validator.Validate(request);

        Assert.False(result.IsValid);
        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(PagedRequest.PageSize), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.PageSizeExceeded, failure.ErrorCode);
    }

    [Fact]
    public void ValidPageSize_AtMax_Succeeds()
    {
        PagedRequest request = new() { PageSize = PagingRules.MaxPageSize };

        ValidationResult result = Validator.Validate(request);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Equal(200, PagingRules.MaxPageSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ValidPage_WhenZeroOrNegative_Fails(int page)
    {
        PagedRequest request = new() { Page = page };

        ValidationResult result = Validator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Equal(nameof(PagedRequest.Page), Assert.Single(result.Errors).PropertyName);
    }

    [Fact]
    public async Task ToPagedResponseAsync_FillsPageInfo()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SeedAsync(dbContext, 412);

        PagedResponse<string> response = await Ordered(dbContext)
            .ToPagedResponseAsync(null, null, CancellationToken.None);

        Assert.Equal(50, response.Items.Count);
        Assert.Equal(1, response.Page.Number);
        Assert.Equal(50, response.Page.Size);
        Assert.Equal(412, response.Page.TotalItems);
        Assert.Equal(9, response.Page.TotalPages);
    }

    [Fact]
    public async Task ToPagedResponseAsync_SecondPageSkipsTheFirst()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SeedAsync(dbContext, 5);

        PagedResponse<string> response = await Ordered(dbContext)
            .ToPagedResponseAsync(2, 2, CancellationToken.None);

        Assert.Equal(["item-003", "item-004"], response.Items);
        Assert.Equal(2, response.Page.Number);
        Assert.Equal(2, response.Page.Size);
        Assert.Equal(5, response.Page.TotalItems);
        Assert.Equal(3, response.Page.TotalPages);
    }

    private static IQueryable<string> Ordered(SparkrockRwcDbContext dbContext) =>
        dbContext.TestEntities
            .AsNoTracking()
            .OrderBy(testEntity => testEntity.TestProperty)
            .ThenBy(testEntity => testEntity.Id)
            .Select(testEntity => testEntity.TestProperty);

    private static async Task SeedAsync(SparkrockRwcDbContext dbContext, int count)
    {
        for (int index = 1; index <= count; index++)
        {
            dbContext.TestEntities.Add(new TestEntity
            {
                Id = Guid.NewGuid(),
                TestProperty = string.Create(CultureInfo.InvariantCulture, $"item-{index:D3}")
            });
        }

        await dbContext.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    ///     Stands in for any slice's <c>Query</c>. The rules under test are rule-builder extensions, so
    ///     they can only be exercised through a closed <see cref="AbstractValidator{T}" />.
    /// </summary>
    public sealed class PagedRequest
    {
        public int? Page { get; init; }

        public int? PageSize { get; init; }
    }

    private sealed class PagedRequestValidator : AbstractValidator<PagedRequest>
    {
        public PagedRequestValidator()
        {
            RuleFor(request => request.Page).ValidPage();
            RuleFor(request => request.PageSize).ValidPageSize();
        }
    }
}
