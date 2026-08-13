using System.Text.Json;
using domain.AttendanceCodes;
using domain.Exceptions;
using domain.Security;
using features.AttendanceCodes;
using infra.persistence.postgre;
using Microsoft.Extensions.Time.Testing;

namespace features.tests.AttendanceCodes;

public sealed class GetAttendanceCodeByIdHandlerTests
{
    [Fact]
    public async Task Handle_ProjectsEveryResponseField()
    {
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();
        Guid codeId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock);
        await AttendanceCodeSeed.AddAsync(
            dbContext,
            codeId,
            value: "E",
            description: "Absent — excused",
            isAbsent: true,
            isExcused: true);

        GetAttendanceCodeById.Response response = await Handle(dbContext, codeId);

        Assert.Equal(codeId, response.Id);
        Assert.Equal("E", response.Value);
        Assert.Equal("Absent — excused", response.Description);
        Assert.True(response.IsAbsent);
        Assert.True(response.IsExcused);
        Assert.True(response.IsActive);
        Assert.Equal(clock.GetUtcNow(), response.CreatedAt);
        Assert.Equal(clock.GetUtcNow(), response.LastUpdatedAt);
    }

    /// <summary>V-21. The interceptor leaves <c>ModifiedAt</c> null until a row is actually modified.</summary>
    [Fact]
    public async Task Handle_WhenNeverModified_ProjectsLastUpdatedFromCreatedAt()
    {
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();
        DateTimeOffset createdAt = clock.GetUtcNow();
        Guid codeId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock);
        await AttendanceCodeSeed.AddAsync(dbContext, codeId);

        GetAttendanceCodeById.Response response = await Handle(dbContext, codeId);

        Assert.Equal(createdAt, response.CreatedAt);
        Assert.Equal(createdAt, response.LastUpdatedAt);
    }

    [Fact]
    public async Task Handle_WhenModified_ProjectsLastUpdatedFromModifiedAt()
    {
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();
        Guid codeId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock);
        AttendanceCode code = await AttendanceCodeSeed.AddAsync(dbContext, codeId);

        clock.Advance(TimeSpan.FromHours(3));
        DateTimeOffset modifiedAt = clock.GetUtcNow();
        code.Description = "Renamed";
        await dbContext.SaveChangesAsync(CancellationToken.None);

        GetAttendanceCodeById.Response response = await Handle(dbContext, codeId);

        Assert.Equal(modifiedAt, response.LastUpdatedAt);
        Assert.NotEqual(modifiedAt, response.CreatedAt);
    }

    /// <summary>
    ///     DEC-19: deactivation hides a code from default <em>list</em> results and nothing more. F08
    ///     has to render history whose code was later deactivated, so the row stays fetchable.
    /// </summary>
    [Fact]
    public async Task Handle_WhenCodeIsInactive_StillReturnsIt()
    {
        Guid codeId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await AttendanceCodeSeed.AddAsync(dbContext, codeId, isActive: false);

        GetAttendanceCodeById.Response response = await Handle(dbContext, codeId);

        Assert.Equal(codeId, response.Id);
        Assert.False(response.IsActive);
    }

    [Fact]
    public async Task Handle_WhenCodeDoesNotExist_ThrowsNotFound()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, Guid.NewGuid()));

        Assert.Equal(ErrorCodes.AttendanceCode.NotFound, exception.ErrorCode);
    }

    /// <summary>
    ///     The aggregate is global (conventions §1, O-03) — no <c>SchoolId</c>, no
    ///     <c>EnsureAuthorized</c>, no <c>WhereAuthorized</c>. A caller with an empty scope still reads
    ///     it. The check this asserts the absence of is exactly the one that gets copied in from the
    ///     Schools slice next door.
    /// </summary>
    [Fact]
    public async Task Handle_AppliesNoTenantScope()
    {
        Guid codeId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await AttendanceCodeSeed.AddAsync(dbContext, codeId);

        GetAttendanceCodeById.Response response = await Handle(dbContext, codeId);

        Assert.Equal(codeId, response.Id);
    }

    /// <summary>
    ///     The other half of the same claim, and the half that can actually fail. A handler holding
    ///     <see cref="ICurrentUser" /> can grow a scope check that
    ///     <see cref="Handle_AppliesNoTenantScope" /> would not notice, because the seeded code is
    ///     visible to whatever identity the test happens to pass. A handler that cannot reach the
    ///     identity cannot scope by it.
    /// </summary>
    [Fact]
    public void QueryHandler_TakesNoCurrentUserDependency()
    {
        Type[] dependencies = typeof(GetAttendanceCodeById.QueryHandler)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(typeof(ICurrentUser), dependencies);
    }

    internal static Task<GetAttendanceCodeById.Response> Handle(SparkrockRwcDbContext dbContext, Guid codeId)
    {
        GetAttendanceCodeById.QueryHandler handler = new(dbContext);

        return handler.Handle(new GetAttendanceCodeById.Query { CodeId = codeId }, CancellationToken.None);
    }
}

/// <summary>
///     The wire shape every AttendanceCodes slice returns.
/// </summary>
public sealed class GetAttendanceCodeByIdResponseTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Spec §2: no nullable members, so no property is ever omitted.</summary>
    [Fact]
    public void Serialize_WritesEveryDocumentedMember()
    {
        string json = JsonSerializer.Serialize(Response(), WebOptions);

        foreach (string member in new[]
                 {
                     "id", "value", "description", "isAbsent", "isExcused", "isActive", "createdAt",
                     "lastUpdatedAt"
                 })
        {
            Assert.Contains($"\"{member}\":", json, StringComparison.Ordinal);
        }
    }

    /// <summary>The legacy identity column never reaches a response (DEC-02).</summary>
    [Fact]
    public void Serialize_NeverWritesTheLegacyId() =>
        Assert.DoesNotContain("legacy", JsonSerializer.Serialize(Response(), WebOptions),
            StringComparison.OrdinalIgnoreCase);

    private static GetAttendanceCodeById.Response Response() => new()
    {
        Id = Guid.NewGuid(),
        Value = AttendanceCodeSeed.DefaultValue,
        Description = AttendanceCodeSeed.DefaultDescription,
        IsAbsent = true,
        IsExcused = false,
        IsActive = true,
        CreatedAt = InMemoryDbContextFactory.DefaultNow,
        LastUpdatedAt = InMemoryDbContextFactory.DefaultNow
    };
}
