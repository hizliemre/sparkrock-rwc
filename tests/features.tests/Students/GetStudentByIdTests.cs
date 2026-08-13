using System.Text.Json;
using domain.Exceptions;
using domain.Students;
using features.Students;
using features.tests.Fakes;
using features.tests.Schools;
using infra.persistence.postgre;
using Microsoft.Extensions.Time.Testing;

namespace features.tests.Students;

public sealed class GetStudentByIdHandlerTests
{
    [Fact]
    public async Task Handle_ProjectsEveryResponseField()
    {
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId, "Demo", "Student01", "09");

        GetStudentById.Response response = await Handle(
            dbContext, schoolId, studentId, FakeCurrentUser.ScopedTo(schoolId));

        Assert.Equal(studentId, response.Id);
        Assert.Equal(schoolId, response.SchoolId);
        Assert.Equal("Demo", response.FirstName);
        Assert.Equal("Student01", response.LastName);
        Assert.Equal("09", response.Grade);
        Assert.True(response.IsActive);
        Assert.Equal(clock.GetUtcNow(), response.CreatedAt);
        Assert.Equal(clock.GetUtcNow(), response.LastUpdatedAt);
    }

    /// <summary>
    ///     Conventions §2: an absent optional field is omitted, never serialised as <c>null</c>.
    /// </summary>
    [Fact]
    public async Task Handle_WhenGradeIsNull_OmitsGradeFromTheResponse()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId, grade: null);

        GetStudentById.Response response = await Handle(
            dbContext, schoolId, studentId, FakeCurrentUser.ScopedTo(schoolId));

        Assert.Null(response.Grade);

        string json = JsonSerializer.Serialize(response, GetStudentByIdResponseTests.WebOptions);

        Assert.DoesNotContain("grade", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     V-21's global projection rule. The interceptor leaves <c>ModifiedAt</c> null until a row is
    ///     actually modified, so an unmodified row would otherwise report no timestamp at all.
    /// </summary>
    [Fact]
    public async Task Handle_WhenNeverModified_ProjectsLastUpdatedFromCreatedAt()
    {
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();
        DateTimeOffset createdAt = clock.GetUtcNow();
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        GetStudentById.Response response = await Handle(
            dbContext, schoolId, studentId, FakeCurrentUser.ScopedTo(schoolId));

        Assert.Equal(createdAt, response.CreatedAt);
        Assert.Equal(createdAt, response.LastUpdatedAt);
    }

    [Fact]
    public async Task Handle_WhenModified_ProjectsLastUpdatedFromModifiedAt()
    {
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        Student student = await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        clock.Advance(TimeSpan.FromHours(3));
        DateTimeOffset modifiedAt = clock.GetUtcNow();
        student.LastName = "Renamed";
        await dbContext.SaveChangesAsync(CancellationToken.None);

        GetStudentById.Response response = await Handle(
            dbContext, schoolId, studentId, FakeCurrentUser.ScopedTo(schoolId));

        Assert.Equal(modifiedAt, response.LastUpdatedAt);
        Assert.NotEqual(modifiedAt, response.CreatedAt);
    }

    /// <summary>
    ///     DEC-19: deactivation hides a student from default <em>list</em> results and nothing more.
    /// </summary>
    [Fact]
    public async Task Handle_WhenStudentIsInactive_StillReturnsIt()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId, isActive: false);

        GetStudentById.Response response = await Handle(
            dbContext, schoolId, studentId, FakeCurrentUser.ScopedTo(schoolId));

        Assert.Equal(studentId, response.Id);
        Assert.False(response.IsActive);
    }

    [Fact]
    public async Task Handle_WhenStudentDoesNotExist_ThrowsNotFound()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, schoolId, Guid.NewGuid(), FakeCurrentUser.ScopedTo(schoolId)));

        Assert.Equal(ErrorCodes.Student.NotFound, exception.ErrorCode);
    }

    /// <summary>
    ///     The route pairs both ids in one predicate. A student reachable through the wrong school's
    ///     path is a tenancy hole, and this is the only test that catches it.
    /// </summary>
    [Fact]
    public async Task Handle_WhenStudentBelongsToAnotherSchool_ThrowsNotFound()
    {
        Guid addressedSchoolId = Guid.NewGuid();
        Guid otherSchoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, addressedSchoolId, name: "Addressed");
        await SchoolSeed.AddAsync(dbContext, otherSchoolId, name: "Other");
        await StudentSeed.AddAsync(dbContext, otherSchoolId, studentId);

        // Authorised for both schools, so nothing but the paired predicate can produce the 404.
        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(
                dbContext,
                addressedSchoolId,
                studentId,
                FakeCurrentUser.ScopedTo(addressedSchoolId, otherSchoolId)));

        Assert.Equal(ErrorCodes.Student.NotFound, exception.ErrorCode);
    }

    /// <summary>
    ///     The existence-oracle rule applied to a path resource: "wrong school" and "no such student"
    ///     are the same query returning no rows.
    /// </summary>
    [Fact]
    public async Task Handle_WhenStudentBelongsToAnotherSchool_ProducesTheSamePayloadAsAbsentStudent()
    {
        Guid addressedSchoolId = Guid.NewGuid();
        Guid otherSchoolId = Guid.NewGuid();
        Guid crossSchoolStudentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, addressedSchoolId, name: "Addressed");
        await SchoolSeed.AddAsync(dbContext, otherSchoolId, name: "Other");
        await StudentSeed.AddAsync(dbContext, otherSchoolId, crossSchoolStudentId);

        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(addressedSchoolId, otherSchoolId);

        NotFoundException crossSchool = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, addressedSchoolId, crossSchoolStudentId, caller));
        NotFoundException genuinelyAbsent = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, addressedSchoolId, Guid.NewGuid(), caller));

        Assert.Equal(genuinelyAbsent.ErrorCode, crossSchool.ErrorCode);
        Assert.Equal(genuinelyAbsent.Message, crossSchool.Message);
    }

    [Fact]
    public async Task Handle_WhenSchoolIsOutsideScope_ThrowsNotFound()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, schoolId, studentId, FakeCurrentUser.ScopedTo(Guid.NewGuid())));

        Assert.Equal(ErrorCodes.School.NotFound, exception.ErrorCode);
    }

    /// <summary>
    ///     A school outside scope and an absent school are the same answer: for a non-admin, an absent
    ///     school is also outside scope, so both fail at <c>EnsureAuthorized</c> with the same code and
    ///     the same message — <see cref="NotFoundException" /> takes no message at all.
    /// </summary>
    [Fact]
    public async Task Handle_WhenSchoolIsOutsideScope_ProducesTheSamePayloadAsAbsentSchool()
    {
        Guid existsButUnauthorized = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, existsButUnauthorized);
        await StudentSeed.AddAsync(dbContext, existsButUnauthorized, studentId);

        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(Guid.NewGuid());

        NotFoundException crossTenant = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, existsButUnauthorized, studentId, caller));
        NotFoundException absentSchool = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, Guid.NewGuid(), studentId, caller));

        Assert.Equal(absentSchool.ErrorCode, crossTenant.ErrorCode);
        Assert.Equal(absentSchool.Message, crossTenant.Message);
    }

    /// <summary>
    ///     DEC-02. On the one aggregate that identifies children, a sequential integer key restores
    ///     exactly the enumeration Guid keys were chosen to prevent.
    /// </summary>
    [Fact]
    public void Response_DeclaresNoLegacyIdMember()
    {
        Assert.DoesNotContain(
            typeof(GetStudentById.Response).GetProperties(),
            property => property.Name.Contains("Legacy", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Design §3 declines to model date of birth at all: name + date of birth + school is the
    ///     highest-value combination in the dataset.
    /// </summary>
    [Fact]
    public void Response_DeclaresNoDateOfBirthMember()
    {
        Assert.DoesNotContain(
            typeof(GetStudentById.Response).GetProperties(),
            property => property.Name.Contains("Birth", StringComparison.OrdinalIgnoreCase));
    }

    private static Task<GetStudentById.Response> Handle(
        SparkrockRwcDbContext dbContext,
        Guid schoolId,
        Guid studentId,
        FakeCurrentUser currentUser)
    {
        GetStudentById.QueryHandler handler = new(dbContext, currentUser);

        return handler.Handle(
            new GetStudentById.Query { SchoolId = schoolId, StudentId = studentId },
            CancellationToken.None);
    }
}

/// <summary>
///     The wire shape of the response every Students slice returns.
/// </summary>
/// <remarks>
///     Conventions §2 requires absent optional fields to be omitted rather than serialised as
///     <c>null</c>. Nothing in the kernel configures <c>WhenWritingNull</c> globally, so the rule is
///     applied per property and is only real if something asserts it.
/// </remarks>
public sealed class GetStudentByIdResponseTests
{
    internal static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Serialize_WhenGradeIsNull_OmitsTheProperty()
    {
        string json = JsonSerializer.Serialize(Response(grade: null), WebOptions);

        Assert.DoesNotContain("grade", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_WhenGradeIsSet_WritesTheProperty()
    {
        string json = JsonSerializer.Serialize(Response(grade: "09"), WebOptions);

        Assert.Contains("\"grade\":\"09\"", json, StringComparison.Ordinal);
    }

    /// <summary>The legacy identity column never reaches a response (DEC-02).</summary>
    [Fact]
    public void Serialize_NeverWritesTheLegacyId()
    {
        string json = JsonSerializer.Serialize(Response(grade: "09"), WebOptions);

        Assert.DoesNotContain("legacy", json, StringComparison.OrdinalIgnoreCase);
    }

    private static GetStudentById.Response Response(string? grade) => new()
    {
        Id = Guid.NewGuid(),
        SchoolId = Guid.NewGuid(),
        FirstName = StudentSeed.DefaultFirstName,
        LastName = StudentSeed.DefaultLastName,
        Grade = grade,
        IsActive = true,
        CreatedAt = InMemoryDbContextFactory.DefaultNow,
        LastUpdatedAt = InMemoryDbContextFactory.DefaultNow
    };
}
