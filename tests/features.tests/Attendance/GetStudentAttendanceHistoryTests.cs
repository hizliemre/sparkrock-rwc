using System.Text.Json;
using domain.Attendance;
using domain.AttendanceCodes;
using domain.Exceptions;
using domain.SchoolTerms;
using domain.Security;
using domain.ValueObjects;
using features.Attendance;
using features.Paging;
using features.tests.AttendanceCodes;
using features.tests.Fakes;
using features.tests.Schools;
using features.tests.Students;
using FluentValidation.Results;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace features.tests.Attendance;

/// <summary>
///     Spec §1's three accepted range forms, and the range check that is the difference between a 400
///     and a 500.
/// </summary>
public sealed class GetStudentAttendanceHistoryValidatorTests
{
    private readonly GetStudentAttendanceHistory.QueryValidator _validator = new();

    [Fact]
    public void Validate_WhenNoRangeGiven_Succeeds()
    {
        ValidationResult result = _validator.Validate(Query());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_WhenSchoolYearGiven_Succeeds()
    {
        ValidationResult result = _validator.Validate(Query(schoolYear: 2026));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_WhenBothDatesGiven_Succeeds()
    {
        ValidationResult result = _validator.Validate(
            Query(from: new DateOnly(2026, 9, 1), toExclusive: new DateOnly(2026, 9, 15)));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    /// <summary>Two range specifications, no defined precedence — spec §1.</summary>
    [Fact]
    public void Validate_WhenSchoolYearAndFromGiven_Fails()
    {
        ValidationResult result = _validator.Validate(
            Query(schoolYear: 2026, from: new DateOnly(2026, 9, 1)));

        Assert.False(result.IsValid);
        Assert.Equal(ErrorCodes.Validation.Failed, Assert.Single(result.Errors).ErrorCode);
    }

    [Fact]
    public void Validate_WhenSchoolYearAndToExclusiveGiven_Fails()
    {
        ValidationResult result = _validator.Validate(
            Query(schoolYear: 2026, toExclusive: new DateOnly(2027, 9, 1)));

        Assert.False(result.IsValid);
        Assert.Equal(ErrorCodes.Validation.Failed, Assert.Single(result.Errors).ErrorCode);
    }

    /// <summary>
    ///     Spec §7: the kernel infers <c>source</c> from the root segment of the failure's property
    ///     path. A cross-field rule declared on <c>RuleFor(query =&gt; query)</c> has an empty path, and
    ///     the inference then falls through to a step that answers <c>query</c> only because a GET
    ///     usually carries no <c>Content-Type</c> — so a client that sends one flips the same violation
    ///     to <c>body</c>.
    /// </summary>
    [Fact]
    public void Validate_WhenSchoolYearAndFromGiven_FailureNamesTheSchoolYearProperty()
    {
        ValidationResult result = _validator.Validate(
            Query(schoolYear: 2026, from: new DateOnly(2026, 9, 1)));

        ValidationFailure failure = Assert.Single(result.Errors);

        Assert.Equal(nameof(GetStudentAttendanceHistory.Query.SchoolYear), failure.PropertyName);
    }

    /// <summary>An open-ended range is an unbounded read dressed as a filter — spec §1.</summary>
    [Fact]
    public void Validate_WhenOnlyFromGiven_Fails()
    {
        ValidationResult result = _validator.Validate(Query(from: new DateOnly(2026, 9, 1)));

        Assert.False(result.IsValid);
        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(GetStudentAttendanceHistory.Query.From), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenOnlyToExclusiveGiven_Fails()
    {
        ValidationResult result = _validator.Validate(Query(toExclusive: new DateOnly(2026, 9, 1)));

        Assert.False(result.IsValid);
        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(GetStudentAttendanceHistory.Query.ToExclusive), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
    }

    /// <summary>Including the equal case, which is an empty half-open range.</summary>
    [Theory]
    [InlineData("2026-09-15", "2026-09-01")]
    [InlineData("2026-09-01", "2026-09-01")]
    public void Validate_WhenFromNotBeforeToExclusive_Fails(string from, string toExclusive)
    {
        ValidationResult result = _validator.Validate(
            Query(from: DateOnly.Parse(from, null), toExclusive: DateOnly.Parse(toExclusive, null)));

        Assert.False(result.IsValid);
        Assert.Equal(ErrorCodes.Validation.Failed, Assert.Single(result.Errors).ErrorCode);
    }

    /// <summary>
    ///     The difference between a 400 and a 500. <c>SchoolYear.FromStartYear</c> throws
    ///     <see cref="ArgumentOutOfRangeException" />, which reaches no <c>IExceptionHandler</c> and
    ///     surfaces as a 500 <c>SYSTEM.UNEXPECTED</c> on the graded-minimum endpoint.
    /// </summary>
    [Fact]
    public void Validate_WhenSchoolYearBelowMinimum_Fails()
    {
        ValidationResult result = _validator.Validate(Query(schoolYear: SchoolYear.MinStartYear - 1));

        Assert.False(result.IsValid);
        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(GetStudentAttendanceHistory.Query.SchoolYear), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenSchoolYearAboveMaximum_Fails()
    {
        ValidationResult result = _validator.Validate(Query(schoolYear: SchoolYear.MaxStartYear + 1));

        Assert.False(result.IsValid);
        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(GetStudentAttendanceHistory.Query.SchoolYear), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenPageSizeExceedsMaximum_Fails()
    {
        ValidationResult result = _validator.Validate(Query(pageSize: PagingRules.MaxPageSize + 1));

        Assert.False(result.IsValid);
        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(GetStudentAttendanceHistory.Query.PageSize), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.PageSizeExceeded, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenPageIsZero_Fails()
    {
        ValidationResult result = _validator.Validate(Query(page: 0));

        Assert.False(result.IsValid);
        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(GetStudentAttendanceHistory.Query.Page), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
    }

    private static GetStudentAttendanceHistory.Query Query(
        int? schoolYear = null,
        DateOnly? from = null,
        DateOnly? toExclusive = null,
        int? page = null,
        int? pageSize = null) => new()
    {
        StudentId = Guid.NewGuid(),
        SchoolYear = schoolYear,
        From = from,
        ToExclusive = toExclusive,
        Page = page,
        PageSize = pageSize
    };
}

/// <summary>
///     F08's handler: the range predicate (V-12, VC-31), the asymmetric authorisation of DEC-16, the
///     cross-school projection, and the snapshot columns.
/// </summary>
public sealed class GetStudentAttendanceHistoryHandlerTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    // ---------------------------------------------------------------- T08-03, the pure range part

    [Fact]
    public void ResolveRange_WhenSchoolYearGiven_UsesToDateRange()
    {
        (DateOnly from, DateOnly toExclusive) = GetStudentAttendanceHistory.ResolveRange(
            2026, null, null, new DateOnly(2030, 1, 1));

        Assert.Equal(new DateOnly(2026, 9, 1), from);
        Assert.Equal(new DateOnly(2027, 9, 1), toExclusive);
    }

    [Fact]
    public void ResolveRange_WhenDatesGiven_UsesThem()
    {
        (DateOnly from, DateOnly toExclusive) = GetStudentAttendanceHistory.ResolveRange(
            null, new DateOnly(2026, 3, 2), new DateOnly(2026, 4, 7), new DateOnly(2030, 1, 1));

        Assert.Equal(new DateOnly(2026, 3, 2), from);
        Assert.Equal(new DateOnly(2026, 4, 7), toExclusive);
    }

    [Fact]
    public void ResolveRange_WhenNothingGiven_UsesSchoolYearOfToday()
    {
        (DateOnly from, DateOnly toExclusive) = GetStudentAttendanceHistory.ResolveRange(
            null, null, null, new DateOnly(2026, 8, 31));

        // 31 August is still the previous school year (DEC-07's September boundary).
        Assert.Equal(new DateOnly(2025, 9, 1), from);
        Assert.Equal(new DateOnly(2026, 9, 1), toExclusive);
    }

    // -------------------------------------------------------- T08-03, school-local "today" (DEC-12)

    /// <summary>
    ///     The test that fails if someone reaches for the clock directly. Both instants are before
    ///     midnight on 31 August in <c>America/Vancouver</c> and after it in UTC, so
    ///     <c>UtcNow.Date</c> resolves school year 2026 and the school-local date resolves 2025.
    /// </summary>
    [Theory]
    [InlineData("2026-09-01T04:00:00Z")] // tasks.md T08-03
    [InlineData("2026-08-31T23:30:00Z")] // spec.md §9, acceptance criterion 4
    public async Task Handle_WhenNoRangeGiven_ResolvesSchoolYearFromSchoolLocalDate(string utcNow)
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeTimeProvider clock = new(DateTimeOffset.Parse(utcNow, null));

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock);
        await SchoolSeed.AddAsync(dbContext, schoolId, timeZoneId: "America/Vancouver");
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        // In school year 2025 only.
        await StudentAttendanceSeed.AddAsync(dbContext, studentId, schoolId, new DateOnly(2025, 9, 15));

        // In school year 2026 only — returned if "today" is resolved in UTC.
        await StudentAttendanceSeed.AddAsync(dbContext, studentId, schoolId, new DateOnly(2026, 9, 15));

        PagedResponse<GetStudentAttendanceHistory.Response> result = await Handle(
            dbContext, Query(studentId), FakeCurrentUser.ScopedTo(schoolId), clock);

        Assert.Equal([new DateOnly(2025, 9, 15)], result.Items.Select(item => item.AttendDate));
    }

    [Fact]
    public async Task Handle_WhenNoRangeGiven_UsesTheStudentsCurrentSchoolTimeZone()
    {
        Guid tokyoSchoolId = Guid.NewGuid();
        Guid vancouverSchoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        // 2026-09-01 13:00 in Asia/Tokyo — school year 2026; 2026-08-31 21:00 in America/Vancouver —
        // school year 2025. The student is at the second, so the second zone is the one that counts.
        FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-09-01T04:00:00Z", null));

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock);
        await SchoolSeed.AddAsync(dbContext, tokyoSchoolId, name: "Tokyo", timeZoneId: "Asia/Tokyo");
        await SchoolSeed.AddAsync(
            dbContext, vancouverSchoolId, name: "Vancouver", timeZoneId: "America/Vancouver");
        await StudentSeed.AddAsync(dbContext, vancouverSchoolId, studentId);

        await StudentAttendanceSeed.AddAsync(
            dbContext, studentId, vancouverSchoolId, new DateOnly(2025, 9, 15));
        await StudentAttendanceSeed.AddAsync(
            dbContext, studentId, vancouverSchoolId, new DateOnly(2026, 9, 15));

        PagedResponse<GetStudentAttendanceHistory.Response> result = await Handle(
            dbContext, Query(studentId), FakeCurrentUser.ScopedTo(vancouverSchoolId), clock);

        Assert.Equal([new DateOnly(2025, 9, 15)], result.Items.Select(item => item.AttendDate));
    }

    // ------------------------------------------------- T08-04, the range predicate and the boundary

    /// <summary>
    ///     Both boundary rows are seeded in one fixture. The half-open upper bound is the divergence,
    ///     and an inclusive one passes every non-boundary assertion.
    /// </summary>
    [Fact]
    public async Task Handle_WhenFilteredBySchoolYear_IncludesFirstDayOfRange()
    {
        (SparkrockRwcDbContext dbContext, Guid schoolId, Guid studentId) = await BoundaryFixtureAsync();

        await using SparkrockRwcDbContext context = dbContext;

        PagedResponse<GetStudentAttendanceHistory.Response> result = await Handle(
            context, Query(studentId, schoolYear: 2026), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Contains(new DateOnly(2026, 9, 1), result.Items.Select(item => item.AttendDate));
    }

    [Fact]
    public async Task Handle_WhenFilteredBySchoolYear_ExcludesFirstDayOfNextRange()
    {
        (SparkrockRwcDbContext dbContext, Guid schoolId, Guid studentId) = await BoundaryFixtureAsync();

        await using SparkrockRwcDbContext context = dbContext;

        PagedResponse<GetStudentAttendanceHistory.Response> result = await Handle(
            context, Query(studentId, schoolYear: 2026), FakeCurrentUser.ScopedTo(schoolId));

        Assert.DoesNotContain(new DateOnly(2027, 9, 1), result.Items.Select(item => item.AttendDate));
    }

    [Fact]
    public async Task Handle_WhenFilteredBySchoolYear_ExcludesPreviousYear()
    {
        (SparkrockRwcDbContext dbContext, Guid schoolId, Guid studentId) = await BoundaryFixtureAsync();

        await using SparkrockRwcDbContext context = dbContext;

        PagedResponse<GetStudentAttendanceHistory.Response> result = await Handle(
            context, Query(studentId, schoolYear: 2026), FakeCurrentUser.ScopedTo(schoolId));

        Assert.DoesNotContain(new DateOnly(2026, 8, 31), result.Items.Select(item => item.AttendDate));
    }

    [Fact]
    public async Task Handle_WhenFilteredByDates_HonoursTheHalfOpenBound()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        await StudentAttendanceSeed.AddAsync(dbContext, studentId, schoolId, new DateOnly(2026, 9, 1));
        await StudentAttendanceSeed.AddAsync(dbContext, studentId, schoolId, new DateOnly(2026, 9, 14));
        await StudentAttendanceSeed.AddAsync(dbContext, studentId, schoolId, new DateOnly(2026, 9, 15));

        PagedResponse<GetStudentAttendanceHistory.Response> result = await Handle(
            dbContext,
            Query(studentId, from: new DateOnly(2026, 9, 1), toExclusive: new DateOnly(2026, 9, 15)),
            FakeCurrentUser.ScopedTo(schoolId));

        Assert.Equal(
            [new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 1)],
            result.Items.Select(item => item.AttendDate));
    }

    [Fact]
    public async Task Handle_OrdersByAttendDateDescending()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        // Inserted against the order, so an unordered query cannot pass by insertion accident.
        await StudentAttendanceSeed.AddAsync(dbContext, studentId, schoolId, new DateOnly(2026, 9, 8));
        await StudentAttendanceSeed.AddAsync(dbContext, studentId, schoolId, new DateOnly(2026, 10, 2));
        await StudentAttendanceSeed.AddAsync(dbContext, studentId, schoolId, new DateOnly(2026, 9, 21));

        PagedResponse<GetStudentAttendanceHistory.Response> result = await Handle(
            dbContext, Query(studentId, schoolYear: 2026), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Equal(
            [new DateOnly(2026, 10, 2), new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 8)],
            result.Items.Select(item => item.AttendDate));
    }

    /// <summary>
    ///     Removed through the change tracker, never by hand-setting <c>IsDeleted</c> (DEC-21): the
    ///     interceptor's delete rewrite is what has to run for this to be a test of the query filter.
    /// </summary>
    [Fact]
    public async Task Handle_ExcludesSoftDeletedRows()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        await StudentAttendanceSeed.AddAsync(dbContext, studentId, schoolId, new DateOnly(2026, 9, 8));
        StudentAttendance withdrawn = await StudentAttendanceSeed.AddAsync(
            dbContext, studentId, schoolId, new DateOnly(2026, 9, 21));

        dbContext.StudentAttendances.Remove(withdrawn);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        PagedResponse<GetStudentAttendanceHistory.Response> result = await Handle(
            dbContext, Query(studentId, schoolYear: 2026), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Equal([new DateOnly(2026, 9, 8)], result.Items.Select(item => item.AttendDate));
        Assert.Equal(1, result.Page.TotalItems);
    }

    /// <summary>An empty history is 200 with an empty envelope, never 404 — the student exists.</summary>
    [Fact]
    public async Task Handle_WhenNoRowsInRange_ReturnsEmptyEnvelope()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        PagedResponse<GetStudentAttendanceHistory.Response> result = await Handle(
            dbContext, Query(studentId, schoolYear: 2026), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Empty(result.Items);
        Assert.Equal(0, result.Page.TotalItems);
        Assert.Equal(0, result.Page.TotalPages);
    }

    /// <summary>This is what fails if someone materialises the query and then filters in memory.</summary>
    [Fact]
    public async Task Handle_TotalItemsCountsTheFilteredSetNotTheLifetime()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        foreach (int startYear in (int[])[2024, 2025, 2026])
        {
            await StudentAttendanceSeed.AddAsync(
                dbContext, studentId, schoolId, new DateOnly(startYear, 10, 1));
            await StudentAttendanceSeed.AddAsync(
                dbContext, studentId, schoolId, new DateOnly(startYear + 1, 3, 1));
        }

        PagedResponse<GetStudentAttendanceHistory.Response> result = await Handle(
            dbContext, Query(studentId, schoolYear: 2025), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Equal(2, result.Page.TotalItems);
        Assert.Equal(2, result.Items.Count);
    }

    // ----------------------------------------------------------------- T08-05, authorisation (§4.1)

    [Fact]
    public async Task Handle_WhenStudentDoesNotExist_ThrowsNotFound()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, Query(Guid.NewGuid()), FakeCurrentUser.ScopedTo(Guid.NewGuid())));

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
        await StudentAttendanceSeed.AddAsync(dbContext, studentId, schoolId, new DateOnly(2026, 9, 8));

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, Query(studentId), FakeCurrentUser.ScopedTo(Guid.NewGuid())));

        Assert.Equal(ErrorCodes.Student.NotFound, exception.ErrorCode);
    }

    /// <summary>
    ///     Byte-identical payloads. Holds by construction because <see cref="NotFoundException" />
    ///     takes no message parameter; this test is what fails the day an overload is added.
    /// </summary>
    [Fact]
    public async Task Handle_WhenStudentOutsideScopeAndWhenAbsent_ProduceIdenticalPayloads()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        FakeCurrentUser stranger = FakeCurrentUser.ScopedTo(Guid.NewGuid());

        NotFoundException outsideScope = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, Query(studentId), stranger));

        NotFoundException absent = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, Query(Guid.NewGuid()), stranger));

        Assert.Equal(absent.ErrorCode, outsideScope.ErrorCode);
        Assert.Equal(absent.Message, outsideScope.Message);
    }

    [Fact]
    public async Task Handle_WhenSystemAdmin_ReadsAnyStudent()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await StudentAttendanceSeed.AddAsync(dbContext, studentId, schoolId, new DateOnly(2026, 9, 8));

        PagedResponse<GetStudentAttendanceHistory.Response> result = await Handle(
            dbContext, Query(studentId, schoolYear: 2026), FakeCurrentUser.SystemAdmin());

        Assert.Equal(1, result.Page.TotalItems);
    }

    /// <summary>
    ///     DEC-16: access follows <c>Student.SchoolId</c>, and the former school loses access at
    ///     transfer — including to the rows it recorded itself.
    /// </summary>
    [Fact]
    public async Task Handle_WhenStudentTransferred_AuthorisesAgainstCurrentSchool()
    {
        Guid formerSchoolId = Guid.NewGuid();
        Guid currentSchoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, formerSchoolId, name: "Former");
        await SchoolSeed.AddAsync(dbContext, currentSchoolId, name: "Current");
        await StudentSeed.AddAsync(dbContext, currentSchoolId, studentId);

        await StudentAttendanceSeed.AddAsync(
            dbContext, studentId, formerSchoolId, new DateOnly(2026, 9, 8));

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, Query(studentId), FakeCurrentUser.ScopedTo(formerSchoolId)));

        Assert.Equal(ErrorCodes.Student.NotFound, exception.ErrorCode);

        PagedResponse<GetStudentAttendanceHistory.Response> result = await Handle(
            dbContext,
            Query(studentId, schoolYear: 2026),
            FakeCurrentUser.ScopedTo(currentSchoolId));

        Assert.Equal(1, result.Page.TotalItems);
    }

    /// <summary>DEC-19: deactivation hides a resource from default list results and nothing else.</summary>
    [Fact]
    public async Task Handle_WhenStudentInactive_ReturnsHistory()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId, isActive: false);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId, isActive: false);
        await StudentAttendanceSeed.AddAsync(dbContext, studentId, schoolId, new DateOnly(2026, 9, 8));

        PagedResponse<GetStudentAttendanceHistory.Response> result = await Handle(
            dbContext, Query(studentId, schoolYear: 2026), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Equal(1, result.Page.TotalItems);
    }

    // ------------------------------------------------------------- T08-06, cross-school rows (§4.2)

    /// <summary>
    ///     <b>The test that fails if someone adds <c>.WhereAuthorized(currentUser)</c> to the history
    ///     query.</b> <see cref="StudentAttendance" /> implements <c>ISchoolScoped</c>, so the call
    ///     compiles and reads as correct; it would silently truncate a transferred student's year at
    ///     the transfer boundary, and the missing rows are exactly the ones a safeguarding question is
    ///     about (spec §4.2, V-07c, DEC-16).
    /// </summary>
    [Fact]
    public async Task Handle_WhenStudentTransferred_ReturnsRowsFromBothSchools()
    {
        Guid formerSchoolId = Guid.NewGuid();
        Guid currentSchoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, formerSchoolId, name: "Former");
        await SchoolSeed.AddAsync(dbContext, currentSchoolId, name: "Current");
        await StudentSeed.AddAsync(dbContext, currentSchoolId, studentId);

        await StudentAttendanceSeed.AddAsync(
            dbContext, studentId, formerSchoolId, new DateOnly(2026, 9, 8));
        await StudentAttendanceSeed.AddAsync(
            dbContext, studentId, currentSchoolId, new DateOnly(2027, 2, 3));

        // Authorised for the current school only — which is the whole point.
        PagedResponse<GetStudentAttendanceHistory.Response> result = await Handle(
            dbContext,
            Query(studentId, schoolYear: 2026),
            FakeCurrentUser.ScopedTo(currentSchoolId));

        Assert.Equal(
            [new DateOnly(2027, 2, 3), new DateOnly(2026, 9, 8)],
            result.Items.Select(item => item.AttendDate));
    }

    [Fact]
    public async Task Handle_WhenRowIsFromAnotherSchool_OriginIsOtherSchool()
    {
        Guid formerSchoolId = Guid.NewGuid();
        Guid currentSchoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, formerSchoolId, name: "Former");
        await SchoolSeed.AddAsync(dbContext, currentSchoolId, name: "Current");
        await StudentSeed.AddAsync(dbContext, currentSchoolId, studentId);
        await StudentAttendanceSeed.AddAsync(
            dbContext, studentId, formerSchoolId, new DateOnly(2026, 9, 8));

        PagedResponse<GetStudentAttendanceHistory.Response> result = await Handle(
            dbContext,
            Query(studentId, schoolYear: 2026),
            FakeCurrentUser.ScopedTo(currentSchoolId));

        Assert.Equal("otherSchool", Assert.Single(result.Items).Origin);
    }

    [Fact]
    public async Task Handle_WhenRowIsFromTheCurrentSchool_OriginIsCurrentSchool()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await StudentAttendanceSeed.AddAsync(dbContext, studentId, schoolId, new DateOnly(2026, 9, 8));

        PagedResponse<GetStudentAttendanceHistory.Response> result = await Handle(
            dbContext, Query(studentId, schoolYear: 2026), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Equal("currentSchool", Assert.Single(result.Items).Origin);
    }

    /// <summary>
    ///     Asserted over the serialised response rather than over the DTO's properties: DEC-16's
    ///     reasoning is that the other school's identity never leaves the process, and a property
    ///     added later would slip past a member-by-member check.
    /// </summary>
    [Fact]
    public async Task Handle_ResponseContainsNoSchoolIdentifier()
    {
        Guid formerSchoolId = Guid.NewGuid();
        Guid currentSchoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, formerSchoolId, name: "Former");
        await SchoolSeed.AddAsync(dbContext, currentSchoolId, name: "Current");
        await StudentSeed.AddAsync(dbContext, currentSchoolId, studentId);
        await StudentAttendanceSeed.AddAsync(
            dbContext, studentId, formerSchoolId, new DateOnly(2026, 9, 8), notes: "Parent phoned.");
        await StudentAttendanceSeed.AddAsync(
            dbContext, studentId, currentSchoolId, new DateOnly(2027, 2, 3), notes: "Parent phoned.");

        PagedResponse<GetStudentAttendanceHistory.Response> result = await Handle(
            dbContext,
            Query(studentId, schoolYear: 2026),
            FakeCurrentUser.ScopedTo(currentSchoolId));

        string json = JsonSerializer.Serialize(result, WebJson);

        Assert.Equal(2, result.Items.Count);
        Assert.DoesNotContain("schoolId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(formerSchoolId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(currentSchoolId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("submissionId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("legacyId", json, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------- T08-07, the projection (§6)

    /// <summary>
    ///     The read-path sibling of F01d's snapshot test (D-02, V-23). No join to
    ///     <c>attendance_codes</c>, ever.
    /// </summary>
    [Fact]
    public async Task Handle_ProjectsSnapshotColumnsNotTheCodeTable()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        Guid codeId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await AttendanceCodeSeed.AddAsync(
            dbContext, codeId, value: "A", description: "Absent — unexcused", isAbsent: true, isExcused: false);

        await StudentAttendanceSeed.AddAsync(
            dbContext,
            studentId,
            schoolId,
            new DateOnly(2026, 9, 8),
            attendanceCodeId: codeId,
            attendCode: "A",
            attendCodeDescription: "Absent — unexcused",
            isAbsent: true,
            isExcused: false);

        AttendanceCode code = await dbContext.AttendanceCodes.SingleAsync(entity => entity.Id == codeId);
        code.Description = "Redefined entirely";
        code.IsAbsent = false;
        code.IsExcused = true;
        await dbContext.SaveChangesAsync(CancellationToken.None);

        PagedResponse<GetStudentAttendanceHistory.Response> result = await Handle(
            dbContext, Query(studentId, schoolYear: 2026), FakeCurrentUser.ScopedTo(schoolId));

        GetStudentAttendanceHistory.Response item = Assert.Single(result.Items);

        Assert.Equal("A", item.AttendCode);
        Assert.Equal("Absent — unexcused", item.AttendCodeDescription);
        Assert.True(item.IsAbsent);
        Assert.False(item.IsExcused);
    }

    /// <summary>DEC-19 states this requirement by name.</summary>
    [Fact]
    public async Task Handle_WhenAttendanceCodeDeactivated_StillRendersTheRow()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        Guid codeId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await AttendanceCodeSeed.AddAsync(dbContext, codeId);
        await StudentAttendanceSeed.AddAsync(
            dbContext, studentId, schoolId, new DateOnly(2026, 9, 8), attendanceCodeId: codeId);

        AttendanceCode code = await dbContext.AttendanceCodes.SingleAsync(entity => entity.Id == codeId);
        code.IsActive = false;
        await dbContext.SaveChangesAsync(CancellationToken.None);

        PagedResponse<GetStudentAttendanceHistory.Response> result = await Handle(
            dbContext, Query(studentId, schoolYear: 2026), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Equal(
            StudentAttendanceSeed.DefaultAttendCodeDescription,
            Assert.Single(result.Items).AttendCodeDescription);
    }

    [Fact]
    public async Task Handle_WhenTermCoversTheDate_ProjectsTermName()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        SchoolTerm term = await StudentAttendanceSeed.AddTermAsync(
            dbContext, schoolId, new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 20), name: "Fall Term");

        await StudentAttendanceSeed.AddAsync(
            dbContext, studentId, schoolId, new DateOnly(2026, 9, 8), termId: term.Id);

        PagedResponse<GetStudentAttendanceHistory.Response> result = await Handle(
            dbContext, Query(studentId, schoolYear: 2026), FakeCurrentUser.ScopedTo(schoolId));

        GetStudentAttendanceHistory.Response item = Assert.Single(result.Items);

        Assert.Equal(term.Id, item.TermId);
        Assert.Equal("Fall Term", item.TermName);
    }

    /// <summary>D-03 keeps a null term legal: legacy's join was a <c>LEFT JOIN</c> and stays one.</summary>
    [Fact]
    public async Task Handle_WhenNoTerm_OmitsTermIdAndTermName()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await StudentAttendanceSeed.AddAsync(dbContext, studentId, schoolId, new DateOnly(2026, 9, 8));

        PagedResponse<GetStudentAttendanceHistory.Response> result = await Handle(
            dbContext, Query(studentId, schoolYear: 2026), FakeCurrentUser.ScopedTo(schoolId));

        GetStudentAttendanceHistory.Response item = Assert.Single(result.Items);

        Assert.Null(item.TermId);
        Assert.Null(item.TermName);

        string json = JsonSerializer.Serialize(result, WebJson);

        Assert.DoesNotContain("termId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("termName", json, StringComparison.Ordinal);
    }

    /// <summary>V-21's global projection rule, with the clock advanced rather than a column set.</summary>
    [Fact]
    public async Task Handle_WhenNeverModified_ProjectsLastUpdatedFromCreatedAt()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        StudentAttendance attendance = await StudentAttendanceSeed.AddAsync(
            dbContext, studentId, schoolId, new DateOnly(2026, 9, 8));

        PagedResponse<GetStudentAttendanceHistory.Response> result = await Handle(
            dbContext, Query(studentId, schoolYear: 2026), FakeCurrentUser.ScopedTo(schoolId), clock);

        Assert.Null(attendance.ModifiedAt);
        Assert.Equal(attendance.CreatedAt, Assert.Single(result.Items).LastUpdatedAt);
    }

    [Fact]
    public async Task Handle_WhenModified_ProjectsLastUpdatedFromModifiedAt()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        StudentAttendance attendance = await StudentAttendanceSeed.AddAsync(
            dbContext, studentId, schoolId, new DateOnly(2026, 9, 8));

        clock.Advance(TimeSpan.FromHours(3));
        attendance.Notes = "Corrected after the parent phoned.";
        await dbContext.SaveChangesAsync(CancellationToken.None);

        PagedResponse<GetStudentAttendanceHistory.Response> result = await Handle(
            dbContext, Query(studentId, schoolYear: 2026), FakeCurrentUser.ScopedTo(schoolId), clock);

        Assert.NotNull(attendance.ModifiedAt);
        Assert.NotEqual(attendance.CreatedAt, attendance.ModifiedAt);
        Assert.Equal(attendance.ModifiedAt, Assert.Single(result.Items).LastUpdatedAt);
    }

    /// <summary>Conventions §2 omits absent optional fields rather than emitting <c>null</c>.</summary>
    [Fact]
    public async Task Handle_WhenNotesAreNull_OmitsTheNotesMember()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await StudentAttendanceSeed.AddAsync(
            dbContext, studentId, schoolId, new DateOnly(2026, 9, 8), notes: null, minutesLate: null);

        PagedResponse<GetStudentAttendanceHistory.Response> result = await Handle(
            dbContext, Query(studentId, schoolYear: 2026), FakeCurrentUser.ScopedTo(schoolId));

        string json = JsonSerializer.Serialize(result, WebJson);

        Assert.DoesNotContain("notes", json, StringComparison.Ordinal);
        Assert.DoesNotContain("minutesLate", json, StringComparison.Ordinal);
    }

    /// <summary><c>notes</c> is returned where it exists — conventions §2, O-17's second branch.</summary>
    [Fact]
    public async Task Handle_WhenNotesArePresent_ProjectsThem()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await StudentAttendanceSeed.AddAsync(
            dbContext,
            studentId,
            schoolId,
            new DateOnly(2026, 9, 8),
            attendCode: "L",
            attendCodeDescription: "Late",
            isAbsent: false,
            isExcused: true,
            minutesLate: 22,
            notes: "Parent phoned at 08:10.");

        PagedResponse<GetStudentAttendanceHistory.Response> result = await Handle(
            dbContext, Query(studentId, schoolYear: 2026), FakeCurrentUser.ScopedTo(schoolId));

        GetStudentAttendanceHistory.Response item = Assert.Single(result.Items);

        Assert.Equal("Parent phoned at 08:10.", item.Notes);
        Assert.Equal(22, item.MinutesLate);
        Assert.False(item.IsAbsent);
        Assert.True(item.IsExcused);
    }

    // ------------------------------------------------------------------------------------- helpers

    /// <summary>
    ///     Both school-year boundary rows plus the day before, in one fixture. Seeded together so an
    ///     inclusive upper bound cannot pass one assertion by failing to reach the other.
    /// </summary>
    private static async Task<(SparkrockRwcDbContext DbContext, Guid SchoolId, Guid StudentId)>
        BoundaryFixtureAsync()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        await StudentAttendanceSeed.AddAsync(dbContext, studentId, schoolId, new DateOnly(2026, 8, 31));
        await StudentAttendanceSeed.AddAsync(dbContext, studentId, schoolId, new DateOnly(2026, 9, 1));
        await StudentAttendanceSeed.AddAsync(dbContext, studentId, schoolId, new DateOnly(2027, 8, 31));
        await StudentAttendanceSeed.AddAsync(dbContext, studentId, schoolId, new DateOnly(2027, 9, 1));

        return (dbContext, schoolId, studentId);
    }

    private static GetStudentAttendanceHistory.Query Query(
        Guid studentId,
        int? schoolYear = null,
        DateOnly? from = null,
        DateOnly? toExclusive = null,
        int? page = null,
        int? pageSize = null) => new()
    {
        StudentId = studentId,
        SchoolYear = schoolYear,
        From = from,
        ToExclusive = toExclusive,
        Page = page,
        PageSize = pageSize
    };

    private static Task<PagedResponse<GetStudentAttendanceHistory.Response>> Handle(
        SparkrockRwcDbContext dbContext,
        GetStudentAttendanceHistory.Query query,
        ICurrentUser currentUser,
        TimeProvider? clock = null) =>
        new GetStudentAttendanceHistory.QueryHandler(
                dbContext,
                currentUser,
                clock ?? new FakeTimeProvider(InMemoryDbContextFactory.DefaultNow))
            .Handle(query, CancellationToken.None);
}
