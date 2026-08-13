using System.Text.Json;
using domain.Attendance;
using domain.Exceptions;
using domain.Security;
using features.Absenteeism;
using features.tests.Fakes;
using features.tests.Schools;
using features.tests.Students;
using FluentValidation.Results;
using infra.persistence.postgre;
using Microsoft.Extensions.Time.Testing;

namespace features.tests.Absenteeism;

public sealed class GetStudentAbsenteeismValidatorTests
{
    private static readonly GetStudentAbsenteeism.QueryValidator Validator = new();

    [Fact]
    public void Validate_WhenSchoolYearAbsent_Succeeds()
    {
        ValidationResult result = Validator.Validate(Query(schoolYear: null));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WhenSchoolYearInRange_Succeeds()
    {
        ValidationResult result = Validator.Validate(Query(2026));

        Assert.True(result.IsValid);
    }

    /// <summary>
    ///     The difference between a 400 and a 500: <c>SchoolYear.FromStartYear</c> throws
    ///     <see cref="ArgumentOutOfRangeException" />, which reaches no <c>IExceptionHandler</c>.
    /// </summary>
    [Fact]
    public void Validate_WhenSchoolYearBelowMinimum_Fails()
    {
        ValidationResult result = Validator.Validate(Query(domain.ValueObjects.SchoolYear.MinStartYear - 1));

        Assert.False(result.IsValid);
        Assert.Equal(ErrorCodes.Validation.Failed, Assert.Single(result.Errors).ErrorCode);
    }

    [Fact]
    public void Validate_WhenSchoolYearAboveMaximum_Fails()
    {
        ValidationResult result = Validator.Validate(Query(domain.ValueObjects.SchoolYear.MaxStartYear + 1));

        Assert.False(result.IsValid);
        Assert.Equal(ErrorCodes.Validation.Failed, Assert.Single(result.Errors).ErrorCode);
    }

    private static GetStudentAbsenteeism.Query Query(int? schoolYear) =>
        new() { StudentId = Guid.NewGuid(), SchoolYear = schoolYear };
}

public sealed class GetStudentAbsenteeismHandlerTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     DEC-12. The clock is 2026-09-01T04:00:00Z; in <c>America/Vancouver</c> (UTC−7) that is
    ///     still 2026-08-31, so the school year is <b>2025</b>. Under <c>UtcNow.Date</c> it would be
    ///     2026 — this is the test that catches a reach for the clock.
    /// </summary>
    [Fact]
    public async Task Handle_WhenSchoolYearAbsent_UsesSchoolYearOfSchoolLocalToday()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 9, 1, 4, 0, 0, TimeSpan.Zero));
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock);
        await SchoolSeed.AddAsync(dbContext, schoolId, timeZoneId: "America/Vancouver");
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await AbsenteeismSeed.SummaryAsync(dbContext, studentId, schoolId, 2025, 7);
        await AbsenteeismSeed.SummaryAsync(dbContext, studentId, schoolId, 2026, 3);

        GetStudentAbsenteeism.Response response = await Handle(dbContext, studentId, null, schoolId, clock);

        Assert.Equal(2025, response.SchoolYear);
        Assert.Equal(7, response.TotalAbsences);
    }

    [Fact]
    public async Task Handle_WhenSummaryExists_ProjectsTotalAbsences()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await AbsenteeismSeed.SummaryAsync(dbContext, studentId, schoolId, 2026, 11);

        GetStudentAbsenteeism.Response response = await Handle(dbContext, studentId, 2026, schoolId);

        Assert.Equal(studentId, response.StudentId);
        Assert.Equal(11, response.TotalAbsences);
    }

    [Fact]
    public async Task Handle_WhenSummaryExists_ProjectsSchoolYearAndLabel()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await AbsenteeismSeed.SummaryAsync(dbContext, studentId, schoolId, 2026, 4);

        GetStudentAbsenteeism.Response response = await Handle(dbContext, studentId, 2026, schoolId);

        Assert.Equal(2026, response.SchoolYear);
        Assert.Equal("2026-2027", response.SchoolYearLabel);
    }

    /// <summary>
    ///     Spec §3. A summary row is created by the first save that records an absence, so a clean
    ///     record has none. The addressed resource is the student, and the student exists.
    /// </summary>
    [Fact]
    public async Task Handle_WhenNoSummaryForTheYear_ReturnsZeroNotNotFound()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        GetStudentAbsenteeism.Response response = await Handle(dbContext, studentId, 2026, schoolId);

        Assert.Equal(0, response.TotalAbsences);
        Assert.False(response.IsChronicallyAbsent);
        Assert.Null(response.LastUpdatedAt);
        Assert.DoesNotContain(
            "lastUpdatedAt",
            JsonSerializer.Serialize(response, WebOptions),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Handle_WhenSummaryExistsForAnotherYear_ReturnsZero()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await AbsenteeismSeed.SummaryAsync(dbContext, studentId, schoolId, 2025, 19);

        GetStudentAbsenteeism.Response response = await Handle(dbContext, studentId, 2026, schoolId);

        Assert.Equal(0, response.TotalAbsences);
    }

    [Fact]
    public async Task Handle_WhenNeverModified_ProjectsLastUpdatedFromCreatedAt()
    {
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        StudentAttendanceSummary summary =
            await AbsenteeismSeed.SummaryAsync(dbContext, studentId, schoolId, 2026, 3);

        GetStudentAbsenteeism.Response response = await Handle(dbContext, studentId, 2026, schoolId, clock);

        Assert.Equal(summary.CreatedAt, response.LastUpdatedAt);
    }

    [Fact]
    public async Task Handle_WhenModified_ProjectsLastUpdatedFromModifiedAt()
    {
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        StudentAttendanceSummary summary =
            await AbsenteeismSeed.SummaryAsync(dbContext, studentId, schoolId, 2026, 3);

        clock.Advance(TimeSpan.FromHours(3));
        summary.TotalAbsences = 4;
        await dbContext.SaveChangesAsync(CancellationToken.None);

        GetStudentAbsenteeism.Response response = await Handle(dbContext, studentId, 2026, schoolId, clock);

        Assert.Equal(clock.GetUtcNow(), response.LastUpdatedAt);
        Assert.NotEqual(summary.CreatedAt, response.LastUpdatedAt);
    }

    /// <summary>
    ///     Spec §1's raw-count boundary. Legacy is <c>&gt;=</c> (<c>sp_GetStudentAttendance:40</c>),
    ///     and an off-by-one here changes which children a school follows up.
    /// </summary>
    [Fact]
    public async Task Handle_WhenAtThreshold_IsChronicallyAbsent()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId, threshold: 10);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await AbsenteeismSeed.SummaryAsync(dbContext, studentId, schoolId, 2026, 10);

        GetStudentAbsenteeism.Response response = await Handle(dbContext, studentId, 2026, schoolId);

        Assert.Equal(10, response.Threshold);
        Assert.True(response.IsChronicallyAbsent);
    }

    [Fact]
    public async Task Handle_WhenBelowThreshold_IsNotChronicallyAbsent()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId, threshold: 10);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await AbsenteeismSeed.SummaryAsync(dbContext, studentId, schoolId, 2026, 9);

        GetStudentAbsenteeism.Response response = await Handle(dbContext, studentId, 2026, schoolId);

        Assert.False(response.IsChronicallyAbsent);
    }

    /// <summary>
    ///     The DEC-16-over-V-17 test. School A (the summary's school of record) sets 20, school B
    ///     (the student's current school) sets 5, and the student has 10 absences. Sourcing the
    ///     threshold the legacy way — <c>summary.SchoolID → Schools</c> — gives 20 and <c>false</c>.
    /// </summary>
    [Fact]
    public async Task Handle_ResolvesThresholdFromTheStudentsCurrentSchool()
    {
        Guid formerSchoolId = Guid.NewGuid();
        Guid currentSchoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, formerSchoolId, name: "Northgate Academy", threshold: 20);
        await SchoolSeed.AddAsync(dbContext, currentSchoolId, name: "Southgate Academy", threshold: 5);
        await StudentSeed.AddAsync(dbContext, currentSchoolId, studentId);
        await AbsenteeismSeed.SummaryAsync(dbContext, studentId, formerSchoolId, 2026, 10);

        GetStudentAbsenteeism.Response response = await Handle(dbContext, studentId, 2026, currentSchoolId);

        Assert.Equal(5, response.Threshold);
        Assert.True(response.IsChronicallyAbsent);
    }

    [Fact]
    public async Task Handle_WhenSchoolThresholdIsNull_UsesTheDomainDefault()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId, threshold: null);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await AbsenteeismSeed.SummaryAsync(dbContext, studentId, schoolId, 2026, 10);

        GetStudentAbsenteeism.Response response = await Handle(dbContext, studentId, 2026, schoolId);

        Assert.Equal(AbsenceRules.DefaultThreshold, response.Threshold);
        Assert.True(response.IsChronicallyAbsent);
    }

    [Fact]
    public async Task Handle_ThresholdSourceIsCurrentSchool()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        GetStudentAbsenteeism.Response response = await Handle(dbContext, studentId, 2026, schoolId);

        Assert.Equal("currentSchool", response.ThresholdSource);
    }

    /// <summary>
    ///     DEC-16 removed <c>thresholdSourceSchoolId</c>: it names the student's <i>current</i>
    ///     school, so returning it to a former school discloses where a child transferred to.
    ///     Asserted on the serialised JSON, because a DTO-level check misses a property added later.
    /// </summary>
    [Fact]
    public async Task Handle_ResponseContainsNoThresholdSourceSchoolId()
    {
        Guid formerSchoolId = Guid.NewGuid();
        Guid currentSchoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, formerSchoolId, name: "Northgate Academy", threshold: 20);
        await SchoolSeed.AddAsync(dbContext, currentSchoolId, name: "Southgate Academy", threshold: 5);
        await StudentSeed.AddAsync(dbContext, currentSchoolId, studentId);
        await AbsenteeismSeed.SummaryAsync(dbContext, studentId, formerSchoolId, 2026, 10);

        GetStudentAbsenteeism.Response response = await Handle(dbContext, studentId, 2026, currentSchoolId);
        string json = JsonSerializer.Serialize(response, WebOptions);

        Assert.DoesNotContain("thresholdSourceSchoolId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("schoolId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Northgate", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Southgate", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(formerSchoolId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(currentSchoolId.ToString(), json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Spec §1: no rate, no percentage, no enrolled-day denominator.</summary>
    [Fact]
    public async Task Handle_ResponseCarriesNoDenominator()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await AbsenteeismSeed.SummaryAsync(dbContext, studentId, schoolId, 2026, 12);

        GetStudentAbsenteeism.Response response = await Handle(dbContext, studentId, 2026, schoolId);
        string json = JsonSerializer.Serialize(response, WebOptions);

        foreach (string banned in new[] { "rate", "percentage", "enrolledDays", "daysPossible" })
            Assert.DoesNotContain(banned, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Handle_WhenAllAbsencesAtCurrentSchool_MarkerIsFalse()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await AbsenteeismSeed.AttendanceAsync(dbContext, studentId, schoolId, new DateOnly(2026, 10, 5));

        GetStudentAbsenteeism.Response response = await Handle(dbContext, studentId, 2026, schoolId);

        Assert.False(response.IncludesOtherSchoolAbsences);
    }

    [Fact]
    public async Task Handle_WhenAnAbsenceAtAnotherSchool_MarkerIsTrue()
    {
        Guid formerSchoolId = Guid.NewGuid();
        Guid currentSchoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, formerSchoolId, name: "Northgate Academy");
        await SchoolSeed.AddAsync(dbContext, currentSchoolId, name: "Southgate Academy");
        await StudentSeed.AddAsync(dbContext, currentSchoolId, studentId);
        await AbsenteeismSeed.AttendanceAsync(dbContext, studentId, formerSchoolId, new DateOnly(2026, 10, 5));

        GetStudentAbsenteeism.Response response = await Handle(dbContext, studentId, 2026, currentSchoolId);

        Assert.True(response.IncludesOtherSchoolAbsences);
    }

    [Fact]
    public async Task Handle_WhenAnotherSchoolsRowIsNotAnAbsence_MarkerIsFalse()
    {
        Guid formerSchoolId = Guid.NewGuid();
        Guid currentSchoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, formerSchoolId, name: "Northgate Academy");
        await SchoolSeed.AddAsync(dbContext, currentSchoolId, name: "Southgate Academy");
        await StudentSeed.AddAsync(dbContext, currentSchoolId, studentId);
        await AbsenteeismSeed.AttendanceAsync(
            dbContext, studentId, formerSchoolId, new DateOnly(2026, 10, 5), isAbsent: false);

        GetStudentAbsenteeism.Response response = await Handle(dbContext, studentId, 2026, currentSchoolId);

        Assert.False(response.IncludesOtherSchoolAbsences);
    }

    /// <summary>
    ///     The range comes from <c>SchoolYear.ToDateRange()</c> against <c>AttendDate</c>, never a
    ///     computed year (V-12, VC-31). 2026-08-31 is in 2025-2026, not 2026-2027.
    /// </summary>
    [Fact]
    public async Task Handle_WhenAnotherSchoolsAbsenceIsOutsideTheYear_MarkerIsFalse()
    {
        Guid formerSchoolId = Guid.NewGuid();
        Guid currentSchoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, formerSchoolId, name: "Northgate Academy");
        await SchoolSeed.AddAsync(dbContext, currentSchoolId, name: "Southgate Academy");
        await StudentSeed.AddAsync(dbContext, currentSchoolId, studentId);
        await AbsenteeismSeed.AttendanceAsync(dbContext, studentId, formerSchoolId, new DateOnly(2026, 8, 31));

        GetStudentAbsenteeism.Response response = await Handle(dbContext, studentId, 2026, currentSchoolId);

        Assert.False(response.IncludesOtherSchoolAbsences);
    }

    [Fact]
    public async Task Handle_MarkerDoesNotIdentifyTheOtherSchool()
    {
        Guid formerSchoolId = Guid.NewGuid();
        Guid currentSchoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, formerSchoolId, name: "Elsewhere Academy");
        await SchoolSeed.AddAsync(dbContext, currentSchoolId, name: "Southgate Academy");
        await StudentSeed.AddAsync(dbContext, currentSchoolId, studentId);
        await AbsenteeismSeed.AttendanceAsync(dbContext, studentId, formerSchoolId, new DateOnly(2026, 10, 5));

        GetStudentAbsenteeism.Response response = await Handle(dbContext, studentId, 2026, currentSchoolId);
        string json = JsonSerializer.Serialize(response, WebOptions);

        Assert.True(response.IncludesOtherSchoolAbsences);
        Assert.DoesNotContain(formerSchoolId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Elsewhere", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Handle_WhenStudentDoesNotExist_ThrowsNotFound()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, Guid.NewGuid(), 2026, schoolId));

        Assert.Equal(ErrorCodes.Student.NotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WhenStudentOutsideScope_ThrowsNotFound()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, studentId, 2026, FakeCurrentUser.ScopedTo(Guid.NewGuid())));

        Assert.Equal(ErrorCodes.Student.NotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WhenStudentOutsideScopeAndWhenAbsent_ProduceIdenticalPayloads()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        NotFoundException outOfScope = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, studentId, 2026, FakeCurrentUser.ScopedTo(Guid.NewGuid())));

        NotFoundException absent = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, Guid.NewGuid(), 2026, FakeCurrentUser.ScopedTo(Guid.NewGuid())));

        Assert.Equal(absent.ErrorCode, outOfScope.ErrorCode);
        Assert.Equal(absent.Message, outOfScope.Message);
    }

    /// <summary>
    ///     DEC-16, "access follows <c>Student.SchoolId</c>". The former school loses access at
    ///     transfer even though the summary still names it as school of record.
    /// </summary>
    [Fact]
    public async Task Handle_WhenStudentTransferred_AuthorisesAgainstCurrentSchool()
    {
        Guid formerSchoolId = Guid.NewGuid();
        Guid currentSchoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, formerSchoolId, name: "Northgate Academy");
        await SchoolSeed.AddAsync(dbContext, currentSchoolId, name: "Southgate Academy");
        await StudentSeed.AddAsync(dbContext, currentSchoolId, studentId);
        await AbsenteeismSeed.SummaryAsync(dbContext, studentId, formerSchoolId, 2026, 2);

        await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, studentId, 2026, FakeCurrentUser.ScopedTo(formerSchoolId)));

        GetStudentAbsenteeism.Response response = await Handle(dbContext, studentId, 2026, currentSchoolId);

        Assert.Equal(2, response.TotalAbsences);
    }

    [Fact]
    public async Task Handle_WhenSystemAdmin_ReadsAnyStudent()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        GetStudentAbsenteeism.Response response =
            await Handle(dbContext, studentId, 2026, FakeCurrentUser.SystemAdmin());

        Assert.Equal(studentId, response.StudentId);
    }

    /// <summary>DEC-19: deactivation hides a resource from default list results only.</summary>
    [Fact]
    public async Task Handle_WhenStudentInactive_ReturnsStatus()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId, isActive: false);
        await AbsenteeismSeed.SummaryAsync(dbContext, studentId, schoolId, 2026, 3);

        GetStudentAbsenteeism.Response response = await Handle(dbContext, studentId, 2026, schoolId);

        Assert.Equal(3, response.TotalAbsences);
    }

    private static Task<GetStudentAbsenteeism.Response> Handle(
        SparkrockRwcDbContext dbContext,
        Guid studentId,
        int? schoolYear,
        Guid authorizedSchoolId,
        TimeProvider? clock = null) =>
        Handle(dbContext, studentId, schoolYear, FakeCurrentUser.ScopedTo(authorizedSchoolId), clock);

    private static Task<GetStudentAbsenteeism.Response> Handle(
        SparkrockRwcDbContext dbContext,
        Guid studentId,
        int? schoolYear,
        ICurrentUser currentUser,
        TimeProvider? clock = null)
    {
        GetStudentAbsenteeism.QueryHandler handler = new(
            dbContext,
            currentUser,
            clock ?? new FakeTimeProvider(InMemoryDbContextFactory.DefaultNow));

        return handler.Handle(
            new GetStudentAbsenteeism.Query { StudentId = studentId, SchoolYear = schoolYear },
            CancellationToken.None);
    }
}
