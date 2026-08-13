using domain.Exceptions;
using domain.Security;
using features.tests.Fakes;

namespace features.tests.Security;

public sealed class WhereAuthorizedTests
{
    private static readonly Guid SchoolA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SchoolB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static IQueryable<ScopedRow> Rows() =>
        new List<ScopedRow>
        {
            new() { SchoolId = SchoolA, Name = "a1" },
            new() { SchoolId = SchoolA, Name = "a2" },
            new() { SchoolId = SchoolB, Name = "b1" }
        }.AsQueryable();

    [Fact]
    public void WhereAuthorized_WhenSystemAdmin_ReturnsEveryRow()
    {
        List<ScopedRow> result = Rows().WhereAuthorized(FakeCurrentUser.SystemAdmin()).ToList();

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void WhereAuthorized_WhenScopedToOneSchool_ReturnsOnlyThatSchoolsRows()
    {
        List<ScopedRow> result = Rows().WhereAuthorized(FakeCurrentUser.ScopedTo(SchoolA)).ToList();

        Assert.Equal(2, result.Count);
        Assert.All(result, row => Assert.Equal(SchoolA, row.SchoolId));
    }

    /// <summary>
    ///     The case that makes the admin short-circuit load-bearing: an empty scope yields no rows, so
    ///     an admin identity with no schools listed would otherwise see nothing at all.
    /// </summary>
    [Fact]
    public void WhereAuthorized_WhenScopeIsEmptyAndNotSystemAdmin_ReturnsNothing()
    {
        List<ScopedRow> result = Rows().WhereAuthorized(new FakeCurrentUser()).ToList();

        Assert.Empty(result);
    }

    /// <summary>
    ///     The short-circuit ignores the scope rather than widening it. An administrator who also
    ///     carries a school list still sees every row — otherwise the scope would silently narrow an
    ///     identity that is supposed to be unrestricted, and the narrowing would be invisible until
    ///     someone assigned a school to an admin account.
    /// </summary>
    [Fact]
    public void WhereAuthorized_WhenSystemAdminIsAlsoScopedToOneSchool_StillReturnsEveryRow()
    {
        FakeCurrentUser scopedAdmin = new() { IsSystemAdmin = true, AuthorizedSchoolIds = [SchoolA] };

        List<ScopedRow> result = Rows().WhereAuthorized(scopedAdmin).ToList();

        Assert.Equal(3, result.Count);
        Assert.Contains(result, row => row.SchoolId == SchoolB);
    }
}

public sealed class EnsureAuthorizedTests
{
    private static readonly Guid SchoolA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SchoolB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    [Fact]
    public void EnsureAuthorized_WhenSchoolInScope_DoesNotThrow()
    {
        FakeCurrentUser.ScopedTo(SchoolA).EnsureAuthorized(SchoolA, "SCHOOL.NOT_FOUND");
    }

    [Fact]
    public void EnsureAuthorized_WhenSystemAdmin_DoesNotThrow()
    {
        FakeCurrentUser.SystemAdmin().EnsureAuthorized(SchoolA, "SCHOOL.NOT_FOUND");
    }

    /// <summary>
    ///     404, never 403. A distinguishable status confirms the record exists, which is the existence
    ///     oracle the tenancy rules are written to close.
    /// </summary>
    [Fact]
    public void EnsureAuthorized_WhenSchoolOutOfScope_ThrowsNotFoundWithTheGivenCode()
    {
        NotFoundException error = Assert.Throws<NotFoundException>(
            () => FakeCurrentUser.ScopedTo(SchoolA).EnsureAuthorized(SchoolB, "SCHOOL.NOT_FOUND"));

        Assert.Equal("SCHOOL.NOT_FOUND", error.ErrorCode);
        Assert.Equal(NotFoundException.NotFoundMessage, error.Message);
    }

    // Deliberately no "does not throw ForbiddenException" test. xUnit's Assert.Throws is exact-type,
    // so the assertion above already fails if the type changes; a second test asserting the negative
    // cannot fail independently of it and only reads as extra coverage.
}
