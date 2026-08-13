using System.Text.Json;
using domain.Attendance;
using domain.Exceptions;
using domain.Schools;
using features.Schools;
using features.tests.Fakes;
using infra.persistence.postgre;
using Microsoft.Extensions.Time.Testing;

namespace features.tests.Schools;

public sealed class GetSchoolByIdHandlerTests
{
    [Fact]
    public async Task Handle_ProjectsEveryResponseField()
    {
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock);
        await SchoolSeed.AddAsync(dbContext, schoolId, name: "Rideau Demo School", threshold: 12);

        GetSchoolById.Response response = await Handle(dbContext, schoolId, FakeCurrentUser.SystemAdmin());

        Assert.Equal(schoolId, response.Id);
        Assert.Equal("Rideau Demo School", response.Name);
        Assert.Equal("America/Toronto", response.TimeZoneId);
        Assert.Equal(12, response.AbsenceAlertThreshold);
        Assert.Equal(12, response.EffectiveAbsenceAlertThreshold);
        Assert.True(response.IsActive);
        Assert.Equal(clock.GetUtcNow(), response.CreatedAt);
        Assert.Equal(clock.GetUtcNow(), response.LastUpdatedAt);
    }

    /// <summary>
    ///     V-21's global projection rule, applied to a second aggregate. The interceptor leaves
    ///     <c>ModifiedAt</c> null until a row is actually modified.
    /// </summary>
    [Fact]
    public async Task Handle_WhenNeverModified_ProjectsLastUpdatedFromCreatedAt()
    {
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();
        DateTimeOffset createdAt = clock.GetUtcNow();
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock);
        await SchoolSeed.AddAsync(dbContext, schoolId);

        GetSchoolById.Response response = await Handle(dbContext, schoolId, FakeCurrentUser.SystemAdmin());

        Assert.Equal(createdAt, response.LastUpdatedAt);
        Assert.Equal(createdAt, response.CreatedAt);
    }

    [Fact]
    public async Task Handle_WhenModified_ProjectsLastUpdatedFromModifiedAt()
    {
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock);
        School school = await SchoolSeed.AddAsync(dbContext, schoolId);

        clock.Advance(TimeSpan.FromHours(3));
        DateTimeOffset modifiedAt = clock.GetUtcNow();
        school.Name = "Renamed";
        await dbContext.SaveChangesAsync(CancellationToken.None);

        GetSchoolById.Response response = await Handle(dbContext, schoolId, FakeCurrentUser.SystemAdmin());

        Assert.Equal(modifiedAt, response.LastUpdatedAt);
        Assert.NotEqual(modifiedAt, response.CreatedAt);
    }

    /// <summary>
    ///     The default comes from <see cref="AbsenceRules.ResolveThreshold" />, never a literal in a
    ///     projection. A second copy of "null means ten" is L-10 reinstated.
    /// </summary>
    [Fact]
    public async Task Handle_WhenThresholdIsNull_ProjectsEffectiveThresholdOfTen()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId, threshold: null);

        GetSchoolById.Response response = await Handle(dbContext, schoolId, FakeCurrentUser.SystemAdmin());

        Assert.Null(response.AbsenceAlertThreshold);
        Assert.Equal(AbsenceRules.DefaultThreshold, response.EffectiveAbsenceAlertThreshold);
    }

    [Fact]
    public async Task Handle_WhenThresholdIsSet_ProjectsItAsEffective()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId, threshold: 3);

        GetSchoolById.Response response = await Handle(dbContext, schoolId, FakeCurrentUser.SystemAdmin());

        Assert.Equal(3, response.AbsenceAlertThreshold);
        Assert.Equal(3, response.EffectiveAbsenceAlertThreshold);
    }

    /// <summary>
    ///     DEC-19: deactivation hides a school from default <em>list</em> results and nothing more.
    /// </summary>
    [Fact]
    public async Task Handle_WhenSchoolIsInactive_StillReturnsIt()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId, isActive: false);

        GetSchoolById.Response response = await Handle(dbContext, schoolId, FakeCurrentUser.SystemAdmin());

        Assert.Equal(schoolId, response.Id);
        Assert.False(response.IsActive);
    }

    [Fact]
    public async Task Handle_WhenSchoolDoesNotExist_ThrowsNotFound()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, Guid.NewGuid(), FakeCurrentUser.SystemAdmin()));

        Assert.Equal(ErrorCodes.School.NotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WhenSchoolIsOutsideScope_ThrowsNotFound()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, schoolId, FakeCurrentUser.ScopedTo(Guid.NewGuid())));

        Assert.Equal(ErrorCodes.School.NotFound, exception.ErrorCode);
    }

    /// <summary>
    ///     The existence oracle is closed by construction — <see cref="NotFoundException" /> takes no
    ///     message. This test is what stops someone adding one.
    /// </summary>
    [Fact]
    public async Task Handle_WhenSchoolIsOutsideScope_ProducesTheSamePayloadAsAbsent()
    {
        Guid existsButUnauthorized = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, existsButUnauthorized);

        Guid absent = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(absent);

        NotFoundException crossTenant = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, existsButUnauthorized, caller));
        NotFoundException genuinelyAbsent = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, absent, caller));

        Assert.Equal(genuinelyAbsent.ErrorCode, crossTenant.ErrorCode);
        Assert.Equal(genuinelyAbsent.Message, crossTenant.Message);
    }

    private static Task<GetSchoolById.Response> Handle(
        SparkrockRwcDbContext dbContext,
        Guid schoolId,
        FakeCurrentUser currentUser)
    {
        GetSchoolById.QueryHandler handler = new(dbContext, currentUser);

        return handler.Handle(new GetSchoolById.Query { SchoolId = schoolId }, CancellationToken.None);
    }
}

/// <summary>
///     The wire shape of the response every Schools slice returns.
/// </summary>
/// <remarks>
///     Conventions §2 requires absent optional fields to be omitted rather than serialised as
///     <c>null</c>. Nothing in the kernel configures <c>WhenWritingNull</c> globally, so the rule is
///     applied per property and is only real if something asserts it.
/// </remarks>
public sealed class GetSchoolByIdResponseTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Serialize_WhenThresholdIsNull_OmitsTheProperty()
    {
        string json = JsonSerializer.Serialize(Response(threshold: null), WebOptions);

        Assert.DoesNotContain("absenceAlertThreshold", json, StringComparison.Ordinal);
        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_WhenThresholdIsSet_WritesTheProperty()
    {
        string json = JsonSerializer.Serialize(Response(threshold: 12), WebOptions);

        Assert.Contains("\"absenceAlertThreshold\":12", json, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Always present, so a client never has to know that a missing threshold means ten.
    /// </summary>
    [Fact]
    public void Serialize_AlwaysWritesTheEffectiveThreshold()
    {
        Assert.Contains(
            FormattableString.Invariant($"\"effectiveAbsenceAlertThreshold\":{AbsenceRules.DefaultThreshold}"),
            JsonSerializer.Serialize(Response(threshold: null), WebOptions),
            StringComparison.Ordinal);
    }

    /// <summary>The legacy identity column never reaches a response (DEC-02).</summary>
    [Fact]
    public void Serialize_NeverWritesTheLegacyId()
    {
        string json = JsonSerializer.Serialize(Response(threshold: 12), WebOptions);

        Assert.DoesNotContain("legacy", json, StringComparison.OrdinalIgnoreCase);
    }

    private static GetSchoolById.Response Response(int? threshold) => new()
    {
        Id = Guid.NewGuid(),
        Name = SchoolSeed.DefaultName,
        TimeZoneId = SchoolSeed.DefaultTimeZoneId,
        AbsenceAlertThreshold = threshold,
        IsActive = true,
        CreatedAt = InMemoryDbContextFactory.DefaultNow,
        LastUpdatedAt = InMemoryDbContextFactory.DefaultNow
    };
}
