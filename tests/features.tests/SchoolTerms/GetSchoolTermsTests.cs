using domain.Exceptions;
using features.Paging;
using features.SchoolTerms;
using features.tests.Fakes;
using features.tests.Schools;
using FluentValidation.Results;
using infra.persistence.postgre;

namespace features.tests.SchoolTerms;

public sealed class GetSchoolTermsValidatorTests
{
    [Fact]
    public void Validate_WhenPageSizeExceedsMaximum_Fails()
    {
        ValidationResult result = Validate(new GetSchoolTerms.Query
        {
            SchoolId = Guid.NewGuid(),
            PageSize = PagingRules.MaxPageSize + 1
        });

        Assert.Equal(
            ErrorCodes.Validation.PageSizeExceeded,
            Assert.Single(result.Errors, failure => failure.PropertyName == nameof(GetSchoolTerms.Query.PageSize))
                .ErrorCode);
    }

    [Fact]
    public void Validate_WhenPageIsZero_Fails()
    {
        ValidationResult result = Validate(new GetSchoolTerms.Query { SchoolId = Guid.NewGuid(), Page = 0 });

        Assert.Equal(
            ErrorCodes.Validation.Failed,
            Assert.Single(result.Errors, failure => failure.PropertyName == nameof(GetSchoolTerms.Query.Page))
                .ErrorCode);
    }

    [Fact]
    public void Validate_WhenPagingIsAbsent_Succeeds() =>
        Assert.True(Validate(new GetSchoolTerms.Query { SchoolId = Guid.NewGuid() }).IsValid);

    private static ValidationResult Validate(GetSchoolTerms.Query query) =>
        new GetSchoolTerms.QueryValidator().Validate(query);
}

public sealed class GetSchoolTermsHandlerTests
{
    /// <summary>
    ///     Chronological is the only order a term list is ever wanted in, and the tie-break on
    ///     <c>Id</c> makes the order total (VC-27). A non-total order under the global
    ///     <c>SplitQuery</c> setting can repeat a row on one page and drop another.
    /// </summary>
    [Fact]
    public async Task Handle_OrdersByStartDateThenId()
    {
        Guid schoolId = Guid.NewGuid();
        Guid earlyTieBreak = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid lateTieBreak = Guid.Parse("22222222-2222-2222-2222-222222222222");

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);

        await SchoolTermSeed.AddAsync(
            dbContext, schoolId, name: "Third",
            startDate: new DateOnly(2027, 4, 1), endDate: new DateOnly(2027, 6, 30));
        await SchoolTermSeed.AddAsync(
            dbContext, schoolId, lateTieBreak, "Second (later id)",
            new DateOnly(2027, 1, 5), new DateOnly(2027, 3, 31));
        await SchoolTermSeed.AddAsync(
            dbContext, schoolId, earlyTieBreak, "Second (earlier id)",
            new DateOnly(2027, 1, 5), new DateOnly(2027, 3, 31));
        await SchoolTermSeed.AddAsync(
            dbContext, schoolId, name: "First",
            startDate: new DateOnly(2026, 9, 1), endDate: new DateOnly(2026, 12, 20));

        PagedResponse<GetSchoolTermById.Response> response = await Handle(
            dbContext, FakeCurrentUser.SystemAdmin(), schoolId);

        Assert.Equal(
            ["First", "Second (earlier id)", "Second (later id)", "Third"],
            response.Items.Select(item => item.Name));
    }

    [Fact]
    public async Task Handle_ByDefaultExcludesInactiveTerms()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await SchoolTermSeed.AddAsync(dbContext, schoolId, name: "Live");
        await SchoolTermSeed.AddAsync(dbContext, schoolId, name: "Fall (superseded)", isActive: false);

        PagedResponse<GetSchoolTermById.Response> response = await Handle(
            dbContext, FakeCurrentUser.SystemAdmin(), schoolId);

        Assert.Equal("Live", Assert.Single(response.Items).Name);
        Assert.Equal(1, response.Page.TotalItems);
    }

    /// <summary>
    ///     Clears <b>O-08</b> for this aggregate. It matters more here than anywhere else:
    ///     deactivation is <em>the</em> mechanism by which a superseded term is parked so a
    ///     replacement can be created over its dates, so a client with no way to list inactive terms
    ///     cannot see why a <c>POST</c> was rejected or find the row to reactivate.
    /// </summary>
    [Fact]
    public async Task Handle_WhenIncludeInactive_ReturnsBoth()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await SchoolTermSeed.AddAsync(dbContext, schoolId, name: "Live");
        await SchoolTermSeed.AddAsync(dbContext, schoolId, name: "Fall (superseded)", isActive: false);

        PagedResponse<GetSchoolTermById.Response> response = await Handle(
            dbContext, FakeCurrentUser.SystemAdmin(), schoolId, includeInactive: true);

        Assert.Equal(2, response.Items.Count);
        Assert.Equal(2, response.Page.TotalItems);
        Assert.Contains(response.Items, item => !item.IsActive);
    }

    [Fact]
    public async Task Handle_ReturnsOnlyTermsOfTheAddressedSchool()
    {
        Guid addressedSchoolId = Guid.NewGuid();
        Guid otherSchoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, addressedSchoolId);
        await SchoolSeed.AddAsync(dbContext, otherSchoolId, name: "Other");
        await SchoolTermSeed.AddAsync(dbContext, addressedSchoolId, name: "Ours");
        await SchoolTermSeed.AddAsync(dbContext, otherSchoolId, name: "Theirs");

        PagedResponse<GetSchoolTermById.Response> response = await Handle(
            dbContext, FakeCurrentUser.SystemAdmin(), addressedSchoolId);

        Assert.Equal("Ours", Assert.Single(response.Items).Name);
    }

    /// <summary>
    ///     The school is an <c>{id}</c> in the path (conventions §2), so a path id that does not
    ///     resolve is a 404. An empty page would report "this school has no terms" about a school
    ///     that does not exist.
    /// </summary>
    [Fact]
    public async Task Handle_WhenSchoolDoesNotExist_ThrowsNotFound()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, FakeCurrentUser.SystemAdmin(), Guid.NewGuid()));

        Assert.Equal(ErrorCodes.School.NotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WhenSchoolIsOutsideScope_ThrowsNotFound()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await SchoolTermSeed.AddAsync(dbContext, schoolId);

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, FakeCurrentUser.ScopedTo(Guid.NewGuid()), schoolId));

        Assert.Equal(ErrorCodes.School.NotFound, exception.ErrorCode);
    }

    /// <summary>
    ///     Byte-identical to the absent-school 404: a caller must not be able to tell a school outside
    ///     their scope from one that was never created.
    /// </summary>
    [Fact]
    public async Task Handle_WhenSchoolIsOutsideScope_ProducesTheSamePayloadAsAbsentSchool()
    {
        Guid existsButUnauthorized = Guid.NewGuid();
        Guid authorized = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, existsButUnauthorized);

        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(authorized);

        NotFoundException crossTenant = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, caller, existsButUnauthorized));
        NotFoundException genuinelyAbsent = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, caller, Guid.NewGuid()));

        Assert.Equal(genuinelyAbsent.ErrorCode, crossTenant.ErrorCode);
        Assert.Equal(genuinelyAbsent.Message, crossTenant.Message);
    }

    /// <summary>
    ///     The difference from the previous two: the school exists and is visible, so this is a 200
    ///     with <c>[]</c> rather than a 404.
    /// </summary>
    [Fact]
    public async Task Handle_WhenSchoolHasNoTerms_ReturnsEmptyItems()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);

        PagedResponse<GetSchoolTermById.Response> response = await Handle(
            dbContext, FakeCurrentUser.ScopedTo(schoolId), schoolId);

        Assert.Empty(response.Items);
        Assert.Equal(0, response.Page.TotalItems);
        Assert.Equal(0, response.Page.TotalPages);
    }

    [Fact]
    public async Task Handle_ReturnsTheCollectionEnvelope()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await SchoolTermSeed.AddAsync(dbContext, schoolId);

        PagedResponse<GetSchoolTermById.Response> response = await Handle(
            dbContext, FakeCurrentUser.SystemAdmin(), schoolId);

        Assert.Equal(PagingRules.DefaultPage, response.Page.Number);
        Assert.Equal(PagingRules.DefaultPageSize, response.Page.Size);
        Assert.Equal(1, response.Page.TotalItems);
        Assert.Equal(1, response.Page.TotalPages);
    }

    /// <summary>
    ///     Paging is real even though a school has three or four terms a year: the second page of a
    ///     one-per-page read must not repeat the first.
    /// </summary>
    [Fact]
    public async Task Handle_WhenPagedOneAtATime_ReturnsSuccessiveTerms()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await SchoolTermSeed.AddAsync(
            dbContext, schoolId, name: "First",
            startDate: new DateOnly(2026, 9, 1), endDate: new DateOnly(2026, 12, 20));
        await SchoolTermSeed.AddAsync(
            dbContext, schoolId, name: "Second",
            startDate: new DateOnly(2027, 1, 5), endDate: new DateOnly(2027, 3, 31));

        PagedResponse<GetSchoolTermById.Response> first = await Handle(
            dbContext, FakeCurrentUser.SystemAdmin(), schoolId, page: 1, pageSize: 1);
        PagedResponse<GetSchoolTermById.Response> second = await Handle(
            dbContext, FakeCurrentUser.SystemAdmin(), schoolId, page: 2, pageSize: 1);

        Assert.Equal("First", Assert.Single(first.Items).Name);
        Assert.Equal("Second", Assert.Single(second.Items).Name);
        Assert.Equal(2, first.Page.TotalPages);
    }

    /// <summary>Past the end is an empty page carrying the real total, not a 404.</summary>
    [Fact]
    public async Task Handle_WhenPageIsPastTheEnd_ReturnsEmptyItemsWithTheTotal()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await SchoolTermSeed.AddAsync(dbContext, schoolId);

        PagedResponse<GetSchoolTermById.Response> response = await Handle(
            dbContext, FakeCurrentUser.SystemAdmin(), schoolId, page: 99);

        Assert.Empty(response.Items);
        Assert.Equal(1, response.Page.TotalItems);
    }

    private static Task<PagedResponse<GetSchoolTermById.Response>> Handle(
        SparkrockRwcDbContext dbContext,
        FakeCurrentUser currentUser,
        Guid schoolId,
        bool includeInactive = false,
        int? page = null,
        int? pageSize = null)
    {
        GetSchoolTerms.QueryHandler handler = new(dbContext, currentUser);

        return handler.Handle(
            new GetSchoolTerms.Query
            {
                SchoolId = schoolId,
                IncludeInactive = includeInactive,
                Page = page,
                PageSize = pageSize
            },
            CancellationToken.None);
    }
}
