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

    [Fact]
    public void WhereAuthorized_WhenSystemAdminWithEmptyScope_StillReturnsEveryRow()
    {
        List<ScopedRow> result = Rows().WhereAuthorized(FakeCurrentUser.SystemAdmin()).ToList();

        Assert.Equal(3, result.Count);
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

    [Fact]
    public void EnsureAuthorized_WhenOutOfScope_DoesNotThrowForbidden()
    {
        Exception error = Record.Exception(
            () => FakeCurrentUser.ScopedTo(SchoolA).EnsureAuthorized(SchoolB, "SCHOOL.NOT_FOUND"));

        Assert.IsNotType<ForbiddenException>(error);
    }
}
