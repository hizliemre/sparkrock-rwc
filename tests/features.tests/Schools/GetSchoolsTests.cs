using domain.Exceptions;
using features.Paging;
using features.Schools;
using features.tests.Fakes;
using FluentValidation.Results;
using infra.persistence.postgre;

namespace features.tests.Schools;

public sealed class GetSchoolsValidatorTests
{
    private readonly GetSchools.QueryValidator _validator = new();

    [Fact]
    public void Validate_WhenPageSizeExceedsMaximum_Fails()
    {
        GetSchools.Query query = new() { PageSize = PagingRules.MaxPageSize + 1 };

        ValidationResult result = _validator.Validate(query);

        Assert.False(result.IsValid);
        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(GetSchools.Query.PageSize), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.PageSizeExceeded, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenPageIsZero_Fails()
    {
        GetSchools.Query query = new() { Page = 0 };

        ValidationResult result = _validator.Validate(query);

        Assert.False(result.IsValid);
        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(GetSchools.Query.Page), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenSearchTermExceeds200_Fails()
    {
        GetSchools.Query query = new() { Q = new string('a', 201) };

        ValidationResult result = _validator.Validate(query);

        Assert.False(result.IsValid);
        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(GetSchools.Query.Q), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenEveryParameterIsAbsent_Succeeds()
    {
        ValidationResult result = _validator.Validate(new GetSchools.Query());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }
}

/// <summary>
///     The <c>?q</c> pattern builder. A pure static function, and therefore testable at the tier where
///     the SQL <c>LIKE</c> itself is not.
/// </summary>
/// <remarks>
///     Unescaped, <c>?q=%</c> returns everything and <c>?q=_</c> matches any single character — a
///     filter the client did not ask for.
/// </remarks>
public sealed class GetSchoolsPatternTests
{
    [Fact]
    public void ToLikePattern_EscapesPercent()
    {
        Assert.Equal("%50\\%%", GetSchools.ToLikePattern("50%"));
    }

    [Fact]
    public void ToLikePattern_EscapesUnderscore()
    {
        Assert.Equal("%a\\_b%", GetSchools.ToLikePattern("a_b"));
    }

    /// <summary>
    ///     Order is the bug. Escaping <c>%</c> before <c>\</c> re-escapes the backslash the first pass
    ///     just introduced, so <c>\%</c> becomes two literal backslashes followed by a live wildcard
    ///     instead of a literal backslash followed by a literal percent.
    /// </summary>
    [Fact]
    public void ToLikePattern_EscapesBackslashFirst()
    {
        Assert.Equal("%\\\\%", GetSchools.ToLikePattern("\\"));
        Assert.Equal("%\\\\\\%%", GetSchools.ToLikePattern("\\%"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToLikePattern_WhenSearchTermIsBlank_ReturnsNull(string? searchTerm)
    {
        Assert.Null(GetSchools.ToLikePattern(searchTerm));
    }
}

public sealed class GetSchoolsHandlerTests
{
    /// <summary>
    ///     The default sort, and it is <b>total</b>. A non-total order under the global
    ///     <c>SplitQuery</c> setting can repeat a row on one page and drop another (VC-27), so every
    ///     default sort ends in <c>Id</c>.
    /// </summary>
    [Fact]
    public async Task Handle_OrdersByNameThenId()
    {
        Guid lowerId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Guid higherId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        Guid alphaId = Guid.Parse("00000000-0000-0000-0000-0000000000ff");

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        // Inserted against both orderings: higher id first so a missing ThenBy(Id) leaves insertion
        // order intact, and the alphabetically-first school last.
        await SchoolSeed.AddAsync(dbContext, higherId, name: "Zeta School");
        await SchoolSeed.AddAsync(dbContext, lowerId, name: "Zeta School");
        await SchoolSeed.AddAsync(dbContext, alphaId, name: "Alpha School");

        PagedResponse<GetSchoolById.Response> result = await Handle(
            dbContext, new GetSchools.Query(), FakeCurrentUser.SystemAdmin());

        Assert.Equal([alphaId, lowerId, higherId], result.Items.Select(school => school.Id));
    }

    [Fact]
    public async Task Handle_ByDefaultExcludesInactiveSchools()
    {
        Guid activeId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, activeId, name: "Active School");
        await SchoolSeed.AddAsync(dbContext, name: "Retired School", isActive: false);

        PagedResponse<GetSchoolById.Response> result = await Handle(
            dbContext, new GetSchools.Query(), FakeCurrentUser.SystemAdmin());

        Assert.Equal(activeId, Assert.Single(result.Items).Id);
        Assert.Equal(1, result.Page.TotalItems);
    }

    [Fact]
    public async Task Handle_WhenIncludeInactive_ReturnsBoth()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, name: "Active School");
        await SchoolSeed.AddAsync(dbContext, name: "Retired School", isActive: false);

        PagedResponse<GetSchoolById.Response> result = await Handle(
            dbContext, new GetSchools.Query { IncludeInactive = true }, FakeCurrentUser.SystemAdmin());

        Assert.Equal(2, result.Items.Count);
        Assert.Contains(result.Items, school => !school.IsActive);
    }

    [Fact]
    public async Task Handle_WhenCallerIsNotAdmin_ReturnsOnlyAuthorizedSchools()
    {
        Guid mine = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, mine, name: "Mine");
        await SchoolSeed.AddAsync(dbContext, name: "Someone Else's");

        PagedResponse<GetSchoolById.Response> result = await Handle(
            dbContext, new GetSchools.Query(), FakeCurrentUser.ScopedTo(mine));

        Assert.Equal(mine, Assert.Single(result.Items).Id);
        Assert.Equal(1, result.Page.TotalItems);
    }

    /// <summary>
    ///     The short-circuit is load-bearing, not an optimisation: an empty scope matches no rows, so
    ///     an administrator with no schools listed would otherwise be shown nothing.
    /// </summary>
    [Fact]
    public async Task Handle_WhenCallerIsAdminWithEmptyScope_ReturnsAll()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, name: "First");
        await SchoolSeed.AddAsync(dbContext, name: "Second");

        FakeCurrentUser admin = FakeCurrentUser.SystemAdmin();
        Assert.Empty(admin.AuthorizedSchoolIds);

        PagedResponse<GetSchoolById.Response> result = await Handle(dbContext, new GetSchools.Query(), admin);

        Assert.Equal(2, result.Items.Count);
    }

    [Fact]
    public async Task Handle_ReturnsTheCollectionEnvelope()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        for (int index = 0; index < 3; index++)
        {
            await SchoolSeed.AddAsync(dbContext, name: $"School {index}");
        }

        PagedResponse<GetSchoolById.Response> result = await Handle(
            dbContext,
            new GetSchools.Query { Page = 2, PageSize = 2 },
            FakeCurrentUser.SystemAdmin());

        Assert.Single(result.Items);
        Assert.Equal(2, result.Page.Number);
        Assert.Equal(2, result.Page.Size);
        Assert.Equal(3, result.Page.TotalItems);
        Assert.Equal(2, result.Page.TotalPages);
    }

    [Fact]
    public async Task Handle_WhenNoSchoolsMatch_ReturnsEmptyItemsNotNotFound()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        PagedResponse<GetSchoolById.Response> result = await Handle(
            dbContext, new GetSchools.Query(), FakeCurrentUser.SystemAdmin());

        Assert.Empty(result.Items);
        Assert.Equal(0, result.Page.TotalItems);
        Assert.Equal(0, result.Page.TotalPages);
    }

    /// <summary>
    ///     Whitespace-only <c>?q</c> applies no filter at all — the pattern builder returns null and
    ///     the predicate is never composed.
    /// </summary>
    /// <remarks>
    ///     This is the only <c>?q</c> assertion available at the handler tier. The SQL <c>LIKE</c> does
    ///     not translate on the in-memory provider, and a test that appeared to prove case-insensitive
    ///     matching here would be proving the opposite of what it claims.
    /// </remarks>
    [Fact]
    public async Task Handle_WhenSearchTermIsWhitespace_AppliesNoFilter()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, name: "Rideau Demo School");
        await SchoolSeed.AddAsync(dbContext, name: "Somerset High");

        PagedResponse<GetSchoolById.Response> result = await Handle(
            dbContext, new GetSchools.Query { Q = "   " }, FakeCurrentUser.SystemAdmin());

        Assert.Equal(2, result.Items.Count);
    }

    private static Task<PagedResponse<GetSchoolById.Response>> Handle(
        SparkrockRwcDbContext dbContext,
        GetSchools.Query query,
        FakeCurrentUser currentUser)
    {
        GetSchools.QueryHandler handler = new(dbContext, currentUser);

        return handler.Handle(query, CancellationToken.None);
    }
}
