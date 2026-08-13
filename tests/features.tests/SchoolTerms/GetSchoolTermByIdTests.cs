using System.Text.Json;
using domain.Exceptions;
using domain.SchoolTerms;
using features.SchoolTerms;
using features.tests.Fakes;
using infra.persistence.postgre;
using Microsoft.Extensions.Time.Testing;

namespace features.tests.SchoolTerms;

public sealed class GetSchoolTermByIdHandlerTests
{
    [Fact]
    public async Task Handle_ProjectsEveryResponseField()
    {
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();
        Guid schoolId = Guid.NewGuid();
        Guid termId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock);
        await SchoolTermSeed.AddAsync(dbContext, schoolId, termId, name: "Autumn");

        GetSchoolTermById.Response response = await Handle(
            dbContext, schoolId, termId, FakeCurrentUser.SystemAdmin());

        Assert.Equal(termId, response.Id);
        Assert.Equal(schoolId, response.SchoolId);
        Assert.Equal("Autumn", response.Name);
        Assert.Equal(SchoolTermSeed.DefaultStart, response.StartDate);
        Assert.Equal(SchoolTermSeed.DefaultEnd, response.EndDate);
        Assert.True(response.IsActive);
        Assert.Equal(clock.GetUtcNow(), response.CreatedAt);
        Assert.Equal(clock.GetUtcNow(), response.LastUpdatedAt);
    }

    /// <summary>V-21: the interceptor leaves <c>ModifiedAt</c> null until a row is actually modified.</summary>
    [Fact]
    public async Task Handle_WhenNeverModified_ProjectsLastUpdatedFromCreatedAt()
    {
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();
        DateTimeOffset createdAt = clock.GetUtcNow();
        Guid schoolId = Guid.NewGuid();
        Guid termId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock);
        await SchoolTermSeed.AddAsync(dbContext, schoolId, termId);

        GetSchoolTermById.Response response = await Handle(
            dbContext, schoolId, termId, FakeCurrentUser.SystemAdmin());

        Assert.Equal(createdAt, response.CreatedAt);
        Assert.Equal(createdAt, response.LastUpdatedAt);
    }

    [Fact]
    public async Task Handle_WhenModified_ProjectsLastUpdatedFromModifiedAt()
    {
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();
        Guid schoolId = Guid.NewGuid();
        Guid termId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock);
        SchoolTerm term = await SchoolTermSeed.AddAsync(dbContext, schoolId, termId);

        clock.Advance(TimeSpan.FromHours(3));
        DateTimeOffset modifiedAt = clock.GetUtcNow();
        term.Name = "Renamed";
        await dbContext.SaveChangesAsync(CancellationToken.None);

        GetSchoolTermById.Response response = await Handle(
            dbContext, schoolId, termId, FakeCurrentUser.SystemAdmin());

        Assert.Equal(modifiedAt, response.LastUpdatedAt);
        Assert.NotEqual(modifiedAt, response.CreatedAt);
    }

    /// <summary>
    ///     DEC-19: deactivation hides a resource from default <em>list</em> results and nothing more.
    ///     F08 must still render history against a superseded term.
    /// </summary>
    [Fact]
    public async Task Handle_WhenTermIsInactive_StillReturnsIt()
    {
        Guid schoolId = Guid.NewGuid();
        Guid termId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolTermSeed.AddAsync(dbContext, schoolId, termId, isActive: false);

        GetSchoolTermById.Response response = await Handle(
            dbContext, schoolId, termId, FakeCurrentUser.SystemAdmin());

        Assert.Equal(termId, response.Id);
        Assert.False(response.IsActive);
    }

    [Fact]
    public async Task Handle_WhenTermDoesNotExist_ThrowsNotFound()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, schoolId, Guid.NewGuid(), FakeCurrentUser.SystemAdmin()));

        Assert.Equal(ErrorCodes.Term.NotFound, exception.ErrorCode);
    }

    /// <summary>
    ///     The route pairs both ids and the query keys on both. A term reachable through another
    ///     school's path is a tenancy hole.
    /// </summary>
    [Fact]
    public async Task Handle_WhenTermBelongsToAnotherSchool_ThrowsNotFound()
    {
        Guid addressedSchoolId = Guid.NewGuid();
        Guid owningSchoolId = Guid.NewGuid();
        Guid termId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolTermSeed.AddAsync(dbContext, owningSchoolId, termId);

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, addressedSchoolId, termId, FakeCurrentUser.SystemAdmin()));

        Assert.Equal(ErrorCodes.Term.NotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WhenSchoolIsOutsideScope_ThrowsNotFound()
    {
        Guid schoolId = Guid.NewGuid();
        Guid termId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolTermSeed.AddAsync(dbContext, schoolId, termId);

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, schoolId, termId, FakeCurrentUser.ScopedTo(Guid.NewGuid())));

        Assert.Equal(ErrorCodes.School.NotFound, exception.ErrorCode);
    }

    /// <summary>
    ///     The existence oracle is closed by construction — <see cref="NotFoundException" /> takes no
    ///     message. This test is what stops someone adding one.
    /// </summary>
    [Fact]
    public async Task Handle_WhenSchoolIsOutsideScope_ProducesTheSamePayloadAsAbsentSchool()
    {
        Guid existsButUnauthorized = Guid.NewGuid();
        Guid termId = Guid.NewGuid();
        Guid authorized = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolTermSeed.AddAsync(dbContext, existsButUnauthorized, termId);

        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(authorized);

        NotFoundException crossTenant = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, existsButUnauthorized, termId, caller));
        NotFoundException genuinelyAbsent = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, Guid.NewGuid(), termId, caller));

        Assert.Equal(genuinelyAbsent.ErrorCode, crossTenant.ErrorCode);
        Assert.Equal(genuinelyAbsent.Message, crossTenant.Message);
    }

    internal static Task<GetSchoolTermById.Response> Handle(
        SparkrockRwcDbContext dbContext,
        Guid schoolId,
        Guid termId,
        FakeCurrentUser currentUser)
    {
        GetSchoolTermById.QueryHandler handler = new(dbContext, currentUser);

        return handler.Handle(
            new GetSchoolTermById.Query { SchoolId = schoolId, TermId = termId },
            CancellationToken.None);
    }
}

/// <summary>
///     The wire shape of the response every SchoolTerms slice returns.
/// </summary>
public sealed class GetSchoolTermByIdResponseTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Conventions §2: calendar dates are ISO 8601, never <c>MM/dd/yyyy</c>.</summary>
    [Fact]
    public void Serialize_WritesDatesAsIso8601()
    {
        string json = JsonSerializer.Serialize(Response(), WebOptions);

        Assert.Contains("\"startDate\":\"2026-09-01\"", json, StringComparison.Ordinal);
        Assert.Contains("\"endDate\":\"2026-12-20\"", json, StringComparison.Ordinal);
    }

    /// <summary>The legacy identity column never reaches a response (DEC-02).</summary>
    [Fact]
    public void Serialize_NeverWritesTheLegacyId() =>
        Assert.DoesNotContain(
            "legacy", JsonSerializer.Serialize(Response(), WebOptions), StringComparison.OrdinalIgnoreCase);

    private static GetSchoolTermById.Response Response() => new()
    {
        Id = Guid.NewGuid(),
        SchoolId = Guid.NewGuid(),
        Name = SchoolTermSeed.DefaultName,
        StartDate = SchoolTermSeed.DefaultStart,
        EndDate = SchoolTermSeed.DefaultEnd,
        IsActive = true,
        CreatedAt = InMemoryDbContextFactory.DefaultNow,
        LastUpdatedAt = InMemoryDbContextFactory.DefaultNow
    };
}
