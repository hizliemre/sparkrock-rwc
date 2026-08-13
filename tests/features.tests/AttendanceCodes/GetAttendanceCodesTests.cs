using domain.Exceptions;
using domain.Security;
using features.AttendanceCodes;
using features.Paging;
using FluentValidation.Results;
using infra.persistence.postgre;

namespace features.tests.AttendanceCodes;

public sealed class GetAttendanceCodesValidatorTests
{
    [Fact]
    public void Validate_WhenPageSizeExceedsMaximum_Fails()
    {
        ValidationResult result = Validate(pageSize: PagingRules.MaxPageSize + 1);

        Assert.False(result.IsValid);
        Assert.Equal(ErrorCodes.Validation.PageSizeExceeded, Assert.Single(result.Errors).ErrorCode);
    }

    [Fact]
    public void Validate_WhenPageIsZero_Fails()
    {
        ValidationResult result = Validate(page: 0);

        Assert.False(result.IsValid);
        Assert.Equal(ErrorCodes.Validation.Failed, Assert.Single(result.Errors).ErrorCode);
    }

    [Fact]
    public void Validate_WhenPagingIsAbsent_Succeeds() => Assert.True(Validate().IsValid);

    [Fact]
    public void Validate_WhenPageSizeIsAtTheMaximum_Succeeds() =>
        Assert.True(Validate(pageSize: PagingRules.MaxPageSize).IsValid);

    private static ValidationResult Validate(int? page = null, int? pageSize = null)
    {
        GetAttendanceCodes.QueryValidator validator = new();

        return validator.Validate(new GetAttendanceCodes.Query { Page = page, PageSize = pageSize });
    }
}

public sealed class GetAttendanceCodesHandlerTests
{
    /// <summary>
    ///     A total order (VC-27). <c>Value</c> alone is unique in the database but the tie-break is
    ///     what the split-query setting needs, and a test that only seeds distinct values never
    ///     exercises it — so the pair here shares a <c>Value</c>, which only an in-memory provider
    ///     permits and which is exactly why the tie-break can be asserted at this tier at all.
    /// </summary>
    [Fact]
    public async Task Handle_OrdersByValueThenId()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        Guid secondOfTheTie = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        Guid firstOfTheTie = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        await AttendanceCodeSeed.AddAsync(dbContext, value: "P");
        await AttendanceCodeSeed.AddAsync(dbContext, secondOfTheTie, value: "L");
        await AttendanceCodeSeed.AddAsync(dbContext, firstOfTheTie, value: "L");
        await AttendanceCodeSeed.AddAsync(dbContext, value: "A");

        PagedResponse<GetAttendanceCodeById.Response> response = await Handle(dbContext);

        Assert.Equal(["A", "L", "L", "P"], response.Items.Select(item => item.Value));
        Assert.Equal(firstOfTheTie, response.Items[1].Id);
        Assert.Equal(secondOfTheTie, response.Items[2].Id);
    }

    [Fact]
    public async Task Handle_ByDefaultExcludesInactiveCodes()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await AttendanceCodeSeed.AddAsync(dbContext, value: "A");
        await AttendanceCodeSeed.AddAsync(dbContext, value: "X", isActive: false);

        PagedResponse<GetAttendanceCodeById.Response> response = await Handle(dbContext);

        Assert.Equal(["A"], response.Items.Select(item => item.Value));
        Assert.Equal(1, response.Page.TotalItems);
    }

    /// <summary>
    ///     O-08 for this aggregate. Deactivated codes must stay listable: DEC-19 requires F08 to render
    ///     history whose code has since been deactivated, and a client needs a way to fetch their
    ///     descriptions.
    /// </summary>
    [Fact]
    public async Task Handle_WhenIncludeInactive_ReturnsBoth()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await AttendanceCodeSeed.AddAsync(dbContext, value: "A");
        await AttendanceCodeSeed.AddAsync(dbContext, value: "X", isActive: false);

        PagedResponse<GetAttendanceCodeById.Response> response = await Handle(dbContext, includeInactive: true);

        Assert.Equal(["A", "X"], response.Items.Select(item => item.Value));
        Assert.Equal(2, response.Page.TotalItems);
    }

    [Fact]
    public async Task Handle_ReturnsTheCollectionEnvelope()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        for (int index = 0; index < 5; index++)
            await AttendanceCodeSeed.AddAsync(dbContext, value: $"C{index}");

        PagedResponse<GetAttendanceCodeById.Response> response = await Handle(dbContext, page: 2, pageSize: 2);

        Assert.Equal(["C2", "C3"], response.Items.Select(item => item.Value));
        Assert.Equal(2, response.Page.Number);
        Assert.Equal(2, response.Page.Size);
        Assert.Equal(5, response.Page.TotalItems);
        Assert.Equal(3, response.Page.TotalPages);
    }

    /// <summary>A page past the end is an empty page, not a 404 and not a wrapped-around first page.</summary>
    [Fact]
    public async Task Handle_WhenPageIsPastTheEnd_ReturnsEmptyItemsAndTheRealTotal()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await AttendanceCodeSeed.AddAsync(dbContext, value: "A");

        PagedResponse<GetAttendanceCodeById.Response> response = await Handle(dbContext, page: 9);

        Assert.Empty(response.Items);
        Assert.Equal(1, response.Page.TotalItems);
    }

    [Fact]
    public async Task Handle_WhenNoCodesExist_ReturnsEmptyItems()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        PagedResponse<GetAttendanceCodeById.Response> response = await Handle(dbContext);

        Assert.Empty(response.Items);
        Assert.Equal(0, response.Page.TotalItems);
        Assert.Equal(0, response.Page.TotalPages);
    }

    [Fact]
    public async Task Handle_AppliesNoTenantScope()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await AttendanceCodeSeed.AddAsync(dbContext, value: "A");

        PagedResponse<GetAttendanceCodeById.Response> response = await Handle(dbContext);

        Assert.Single(response.Items);
    }

    /// <summary>See <c>GetAttendanceCodeByIdHandlerTests.QueryHandler_TakesNoCurrentUserDependency</c>.</summary>
    [Fact]
    public void QueryHandler_TakesNoCurrentUserDependency()
    {
        Type[] dependencies = typeof(GetAttendanceCodes.QueryHandler)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(typeof(ICurrentUser), dependencies);
    }

    private static Task<PagedResponse<GetAttendanceCodeById.Response>> Handle(
        SparkrockRwcDbContext dbContext,
        int? page = null,
        int? pageSize = null,
        bool includeInactive = false)
    {
        GetAttendanceCodes.QueryHandler handler = new(dbContext);

        return handler.Handle(
            new GetAttendanceCodes.Query { Page = page, PageSize = pageSize, IncludeInactive = includeInactive },
            CancellationToken.None);
    }
}
