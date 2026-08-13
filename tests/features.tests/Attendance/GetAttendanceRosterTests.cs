using System.Globalization;
using System.Text.Json;
using Carter;
using domain.Attendance;
using domain.AttendanceCodes;
using domain.Exceptions;
using features.Attendance;
using features.Paging;
using features.Students;
using features.tests.AttendanceCodes;
using features.tests.Fakes;
using features.tests.Schools;
using features.tests.Students;
using FluentValidation.Results;
using infra.persistence.postgre;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace features.tests.Attendance;

/// <summary>
///     <c>{date}</c> binds as a <c>string</c> and is validated here, so a malformed date is a 400
///     rather than a routing 404 (spec §6).
/// </summary>
public sealed class GetAttendanceRosterValidatorTests
{
    private readonly GetAttendanceRoster.QueryValidator _validator = new();

    [Fact]
    public void Validate_WhenDateIsIso_Succeeds()
    {
        ValidationResult result = _validator.Validate(Query(date: "2026-09-14"));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    /// <summary>
    ///     Conventions §2 rejects <c>MM/dd/yyyy</c>, and <c>DateOnly.TryParse</c> accepts it under the
    ///     invariant culture. This test is what forces <c>TryParseExact</c> with a single pattern.
    /// </summary>
    [Fact]
    public void Validate_WhenDateIsUsFormat_Fails()
    {
        ValidationResult result = _validator.Validate(Query(date: "09/14/2026"));

        Assert.False(result.IsValid);
        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenDateIsImpossible_Fails()
    {
        ValidationResult result = _validator.Validate(Query(date: "2026-13-01"));

        Assert.False(result.IsValid);
        Assert.Equal(ErrorCodes.Validation.Failed, Assert.Single(result.Errors).ErrorCode);
    }

    [Fact]
    public void Validate_WhenDateIsEmpty_Fails()
    {
        ValidationResult result = _validator.Validate(Query(date: string.Empty));

        Assert.False(result.IsValid);
        Assert.Equal(ErrorCodes.Validation.Failed, Assert.Single(result.Errors).ErrorCode);
    }

    /// <summary>
    ///     Spec §6's naming rule under test. <c>Date</c> camel-cases to <c>date</c>, which is the route
    ///     value key, which is what makes <c>api/Errors/ViolationSource</c> infer <c>"path"</c>. Rename
    ///     the property and this is the only thing that fails.
    /// </summary>
    /// <remarks>
    ///     Deliberately not named <c>…_ReportsPathSource</c>: <c>ViolationSource.For</c> is
    ///     <c>internal</c> to <c>api</c> and takes an <c>HttpRequest</c>, so the inference itself is
    ///     unreachable from this project (plan, "Testing tiers").
    /// </remarks>
    [Fact]
    public void Validate_WhenDateIsInvalid_FailureNamesTheDateProperty()
    {
        ValidationResult result = _validator.Validate(Query(date: "2026-13-01"));

        // The literal, deliberately not nameof(Query.Date): nameof renames with the property, so the
        // one test that exists to fail on a rename would follow it and stay green. The string that
        // matters is the one the route template spells, and it is spelled here.
        Assert.Equal("Date", Assert.Single(result.Errors).PropertyName);
    }

    [Fact]
    public void Validate_WhenPageSizeExceedsMaximum_Fails()
    {
        ValidationResult result = _validator.Validate(Query(pageSize: PagingRules.MaxPageSize + 1));

        Assert.False(result.IsValid);
        ValidationFailure failure = Assert.Single(result.Errors);

        // Literal for the same reason as Validate_WhenDateIsInvalid_FailureNamesTheDateProperty:
        // PageSize camel-cases to pageSize, which is the query key ViolationSource matches on.
        Assert.Equal("PageSize", failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.PageSizeExceeded, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenPageIsZero_Fails()
    {
        ValidationResult result = _validator.Validate(Query(page: 0));

        Assert.False(result.IsValid);
        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal("Page", failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenGradeIsAbsent_Succeeds()
    {
        ValidationResult result = _validator.Validate(Query());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    /// <summary>
    ///     <b>V-24.</b> An empty grade is a valid request meaning all grades, and is the literal value
    ///     legacy always sent (L-15).
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WhenGradeIsEmpty_Succeeds(string grade)
    {
        ValidationResult result = _validator.Validate(Query(grade: grade));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    private static GetAttendanceRoster.Query Query(
        string date = "2026-09-14",
        string? grade = null,
        int? page = null,
        int? pageSize = null) => new()
    {
        SchoolId = Guid.NewGuid(),
        Date = date,
        Grade = grade,
        Page = page,
        PageSize = pageSize
    };
}

public sealed class GetAttendanceRosterHandlerTests
{
    // ------------------------------------------------------------------ the roster

    [Fact]
    public async Task Handle_ReturnsActiveStudentsOfTheSchool()
    {
        Guid schoolId = Guid.NewGuid();
        Guid otherSchoolId = Guid.NewGuid();
        Guid mineId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId, name: "Addressed");
        await SchoolSeed.AddAsync(dbContext, otherSchoolId, name: "Other");
        await StudentSeed.AddAsync(dbContext, schoolId, mineId, lastName: "Mine");
        await StudentSeed.AddAsync(dbContext, otherSchoolId, lastName: "Theirs");

        // Authorised for both, so only the SchoolId predicate can keep the other school's roster out.
        PagedResponse<GetAttendanceRoster.Response> result = await Handle(
            dbContext, Query(schoolId), FakeCurrentUser.ScopedTo(schoolId, otherSchoolId));

        Assert.Equal(mineId, Assert.Single(result.Items).StudentId);
        Assert.Equal(1, result.Page.TotalItems);
    }

    /// <summary>
    ///     D-06's inferred contract: the roster is active students only. The honest consequence is in
    ///     spec §5 — F07 accepts a submission for a student F06 will not list.
    /// </summary>
    [Fact]
    public async Task Handle_ExcludesInactiveStudents()
    {
        Guid schoolId = Guid.NewGuid();
        Guid activeId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, activeId, lastName: "Active");
        await StudentSeed.AddAsync(dbContext, schoolId, lastName: "Withdrawn", isActive: false);

        PagedResponse<GetAttendanceRoster.Response> result = await Handle(
            dbContext, Query(schoolId), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Equal(activeId, Assert.Single(result.Items).StudentId);
        Assert.Equal(1, result.Page.TotalItems);
    }

    /// <summary>
    ///     The legacy grid's order, and it is <b>total</b>: <c>UseQuerySplittingBehavior(SplitQuery)</c>
    ///     is set globally and a non-total order can repeat a row on one page and drop another (VC-27).
    /// </summary>
    [Fact]
    public async Task Handle_OrdersByLastNameThenFirstNameThenId()
    {
        Guid schoolId = Guid.NewGuid();
        Guid lowerId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Guid bobId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        Guid higherId = Guid.Parse("00000000-0000-0000-0000-000000000003");
        Guid youngId = Guid.Parse("00000000-0000-0000-0000-0000000000ff");

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);

        // Inserted against every ordering at once: the alphabetically-last surname first, the
        // first-name tie-break out of order, and the id tie-break with the higher id inserted first.
        await StudentSeed.AddAsync(dbContext, schoolId, youngId, firstName: "Zoe", lastName: "Young");
        await StudentSeed.AddAsync(dbContext, schoolId, bobId, firstName: "Bob", lastName: "Adams");
        await StudentSeed.AddAsync(dbContext, schoolId, higherId, firstName: "Anna", lastName: "Adams");
        await StudentSeed.AddAsync(dbContext, schoolId, lowerId, firstName: "Anna", lastName: "Adams");

        PagedResponse<GetAttendanceRoster.Response> result = await Handle(
            dbContext, Query(schoolId), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Equal(
            [lowerId, higherId, bobId, youngId],
            result.Items.Select(row => row.StudentId));
    }

    [Fact]
    public async Task Handle_WhenNoStudents_ReturnsEmptyEnvelope()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);

        PagedResponse<GetAttendanceRoster.Response> result = await Handle(
            dbContext, Query(schoolId), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Empty(result.Items);
        Assert.Equal(0, result.Page.TotalItems);
        Assert.Equal(0, result.Page.TotalPages);
    }

    [Fact]
    public async Task Handle_ReturnsTheCollectionEnvelope()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        for (int index = 0; index < 3; index++)
        {
            await StudentSeed.AddAsync(
                dbContext, schoolId, lastName: FormattableString.Invariant($"Student{index}"));
        }

        PagedResponse<GetAttendanceRoster.Response> result = await Handle(
            dbContext, Query(schoolId, page: 2, pageSize: 2), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Single(result.Items);
        Assert.Equal(2, result.Page.Number);
        Assert.Equal(2, result.Page.Size);
        Assert.Equal(3, result.Page.TotalItems);
        Assert.Equal(2, result.Page.TotalPages);
    }

    // ------------------------------------------------------- ?grade= — V-24, identical to F05

    [Fact]
    public async Task Handle_WhenGradeFilterAbsent_ReturnsAllGrades()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        Guid schoolId = await SeedGradesAsync(dbContext);

        PagedResponse<GetAttendanceRoster.Response> result = await Handle(
            dbContext, Query(schoolId), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Equal(SeededGradeCount, result.Page.TotalItems);
    }

    /// <summary>
    ///     <b>V-24's <c>Verified by</c>.</b> The empty string is the literal value legacy always sent
    ///     (L-15); treating it as "match students whose grade is the empty string" would return nothing
    ///     and reproduce L-15's silence with a different mechanism.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_WhenGradeFilterIsEmpty_ReturnsAllGrades(string grade)
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        Guid schoolId = await SeedGradesAsync(dbContext);

        PagedResponse<GetAttendanceRoster.Response> result = await Handle(
            dbContext, Query(schoolId, grade: grade), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Equal(SeededGradeCount, result.Page.TotalItems);
    }

    [Fact]
    public async Task Handle_WhenGradeFilterSupplied_ReturnsOnlyThatGrade()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        Guid schoolId = await SeedGradesAsync(dbContext);

        PagedResponse<GetAttendanceRoster.Response> result = await Handle(
            dbContext, Query(schoolId, grade: "09"), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Equal("09", Assert.Single(result.Items).Grade);
    }

    /// <summary>
    ///     A null grade cannot equal a requested one, and <c>IS NULL</c> matching would make
    ///     <c>?grade=07</c> return ungraded students. There is no <c>?grade=none</c> sentinel.
    /// </summary>
    [Fact]
    public async Task Handle_WhenGradeFilterSupplied_ExcludesStudentsWithNullGrade()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        Guid schoolId = await SeedGradesAsync(dbContext);

        PagedResponse<GetAttendanceRoster.Response> result = await Handle(
            dbContext, Query(schoolId, grade: "09"), FakeCurrentUser.ScopedTo(schoolId));

        Assert.DoesNotContain(result.Items, row => row.Grade is null);
    }

    /// <summary>A surrounding-whitespace <c>?grade=</c> still names a grade, and is trimmed.</summary>
    [Fact]
    public async Task Handle_WhenGradeFilterIsPadded_TrimsBeforeMatching()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        Guid schoolId = await SeedGradesAsync(dbContext);

        PagedResponse<GetAttendanceRoster.Response> result = await Handle(
            dbContext, Query(schoolId, grade: " 09 "), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Equal("09", Assert.Single(result.Items).Grade);
    }

    /// <summary>
    ///     Exact and ordinal, no case folding. <c>Grade</c> is <c>varchar(10)</c> free text with no
    ///     vocabulary (F01c §3), so anything cleverer is a guess — and a case-insensitive match here
    ///     would silently disagree with F05's list for the same query string.
    /// </summary>
    [Fact]
    public async Task Handle_WhenGradeFilterDiffersByCase_MatchesNothing()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, lastName: "Kay", grade: "K");

        PagedResponse<GetAttendanceRoster.Response> result = await Handle(
            dbContext, Query(schoolId, grade: "k"), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Empty(result.Items);
        Assert.Equal(0, result.Page.TotalItems);
    }

    /// <summary>Exact match, never prefix: <c>?grade=1</c> must not return grades 10, 11 and 12.</summary>
    [Fact]
    public async Task Handle_WhenGradeFilterSupplied_MatchesExactlyNotByPrefix()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, lastName: "Ten", grade: "10");
        await StudentSeed.AddAsync(dbContext, schoolId, lastName: "Eleven", grade: "11");

        PagedResponse<GetAttendanceRoster.Response> result = await Handle(
            dbContext, Query(schoolId, grade: "1"), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Empty(result.Items);
    }

    // ------------------------------------------- the three-state status discriminator (T06-04)

    [Fact]
    public async Task Handle_WhenNoAttendanceRecorded_StatusIsNotRecordedAndAttendanceIsAbsent()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId);

        PagedResponse<GetAttendanceRoster.Response> result = await Handle(
            dbContext, Query(schoolId), FakeCurrentUser.ScopedTo(schoolId));

        GetAttendanceRoster.Response row = Assert.Single(result.Items);
        Assert.Equal(GetAttendanceRoster.StatusNotRecorded, row.Status);
        Assert.Null(row.Attendance);
    }

    [Fact]
    public async Task Handle_WhenRecordedWithNote_StatusIsRecordedAndNotesArePresent()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        Guid attendanceId = Guid.NewGuid();
        Guid termId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await AddAttendanceAsync(
            dbContext, studentId, schoolId, RosterDate, attendanceId,
            notes: "Parent phoned at 08:10.", minutesLate: 12, termId: termId);

        PagedResponse<GetAttendanceRoster.Response> result = await Handle(
            dbContext, Query(schoolId), FakeCurrentUser.ScopedTo(schoolId));

        GetAttendanceRoster.Response row = Assert.Single(result.Items);
        Assert.Equal(GetAttendanceRoster.StatusRecorded, row.Status);
        GetAttendanceRoster.Response.AttendanceDetail detail = Assert.IsType<
            GetAttendanceRoster.Response.AttendanceDetail>(row.Attendance);
        Assert.Equal(attendanceId, detail.AttendanceId);
        Assert.Equal("Parent phoned at 08:10.", detail.Notes);
        Assert.Equal(12, detail.MinutesLate);
        Assert.Equal(termId, detail.TermId);
        Assert.Equal(DefaultCode, detail.AttendCode);
        Assert.Equal(DefaultCodeDescription, detail.AttendCodeDescription);
        Assert.True(detail.IsAbsent);
        Assert.False(detail.IsExcused);
    }

    /// <summary>
    ///     <b>Criterion 4, and the distinction O-17 turns on.</b> "Recorded with no note" and "not yet
    ///     recorded" must not be confusable: the first has an <c>attendance</c> object with no
    ///     <c>notes</c> key, the second has no <c>attendance</c> object at all.
    /// </summary>
    [Fact]
    public async Task Handle_WhenRecordedWithoutNote_StatusIsRecordedAndNotesAreOmitted()
    {
        Guid schoolId = Guid.NewGuid();
        Guid recordedId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, recordedId, lastName: "Recorded");
        await StudentSeed.AddAsync(dbContext, schoolId, lastName: "Untouched");
        await AddAttendanceAsync(dbContext, recordedId, schoolId, RosterDate, notes: null);

        PagedResponse<GetAttendanceRoster.Response> result = await Handle(
            dbContext, Query(schoolId), FakeCurrentUser.ScopedTo(schoolId));

        GetAttendanceRoster.Response recorded = result.Items.Single(row => row.StudentId == recordedId);
        GetAttendanceRoster.Response notRecorded = result.Items.Single(row => row.StudentId != recordedId);

        Assert.Equal(GetAttendanceRoster.StatusRecorded, recorded.Status);
        Assert.NotNull(recorded.Attendance);
        Assert.Null(recorded.Attendance.Notes);
        Assert.Equal(GetAttendanceRoster.StatusNotRecorded, notRecorded.Status);
        Assert.Null(notRecorded.Attendance);

        string recordedJson = JsonSerializer.Serialize(recorded, WebOptions);
        string notRecordedJson = JsonSerializer.Serialize(notRecorded, WebOptions);

        Assert.Contains("\"status\":\"recorded\"", recordedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("notes", recordedJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"attendance\":", recordedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("attendance", notRecordedJson, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     A soft-deleted correction reads as <c>notRecorded</c> — the correct answer, since F07 can
    ///     write the day again. The reflective filter does it (VC-13); nothing in this slice mentions
    ///     <c>IsDeleted</c>.
    /// </summary>
    [Fact]
    public async Task Handle_WhenAttendanceSoftDeleted_StatusIsNotRecorded()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        StudentAttendance attendance = await AddAttendanceAsync(dbContext, studentId, schoolId, RosterDate);

        // Remove(), never a hand-set IsDeleted (DEC-21): the interceptor rewrites the delete.
        dbContext.StudentAttendances.Remove(attendance);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        PagedResponse<GetAttendanceRoster.Response> result = await Handle(
            dbContext, Query(schoolId), FakeCurrentUser.ScopedTo(schoolId));

        GetAttendanceRoster.Response row = Assert.Single(result.Items);
        Assert.Equal(GetAttendanceRoster.StatusNotRecorded, row.Status);
        Assert.Null(row.Attendance);
    }

    /// <summary>The join is keyed on the date as well as the student.</summary>
    [Fact]
    public async Task Handle_WhenAttendanceIsForAnotherDate_StatusIsNotRecorded()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await AddAttendanceAsync(dbContext, studentId, schoolId, RosterDate.AddDays(-1));

        PagedResponse<GetAttendanceRoster.Response> result = await Handle(
            dbContext, Query(schoolId), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Equal(GetAttendanceRoster.StatusNotRecorded, Assert.Single(result.Items).Status);
    }

    /// <summary>
    ///     <b>D-02's read half.</b> Redefining a code leaves history alone, because the roster reads the
    ///     four snapshot columns on <c>student_attendances</c> and never joins <c>attendance_codes</c>.
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
        AttendanceCode code = await AttendanceCodeSeed.AddAsync(
            dbContext, codeId, value: "A", description: "Absent — unexcused", isAbsent: true, isExcused: false);
        await AddAttendanceAsync(
            dbContext, studentId, schoolId, RosterDate, attendanceCodeId: codeId,
            attendCode: code.Value, attendCodeDescription: code.Description,
            isAbsent: code.IsAbsent, isExcused: code.IsExcused);

        code.Description = "Redefined entirely";
        code.IsAbsent = false;
        await dbContext.SaveChangesAsync(CancellationToken.None);

        PagedResponse<GetAttendanceRoster.Response> result = await Handle(
            dbContext, Query(schoolId), FakeCurrentUser.ScopedTo(schoolId));

        GetAttendanceRoster.Response.AttendanceDetail detail =
            Assert.Single(result.Items).Attendance ?? throw new InvalidOperationException("no attendance");
        Assert.Equal("Absent — unexcused", detail.AttendCodeDescription);
        Assert.True(detail.IsAbsent);
    }

    // ------------------------------------------------------------ recordedElsewhere (T06-05)

    /// <summary>
    ///     The join is on <c>(StudentId, AttendDate)</c> and deliberately not on <c>SchoolId</c> (V-06),
    ///     so a row written by the student's previous school is reported rather than hidden.
    /// </summary>
    [Fact]
    public async Task Handle_WhenRowBelongsToAnotherSchool_StatusIsRecordedElsewhere()
    {
        Guid schoolId = Guid.NewGuid();
        Guid formerSchoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId, name: "Receiving");
        await SchoolSeed.AddAsync(dbContext, formerSchoolId, name: "Former");
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await AddAttendanceAsync(dbContext, studentId, formerSchoolId, RosterDate, notes: "Former school note.");

        PagedResponse<GetAttendanceRoster.Response> result = await Handle(
            dbContext, Query(schoolId), FakeCurrentUser.ScopedTo(schoolId));

        GetAttendanceRoster.Response row = Assert.Single(result.Items);
        Assert.Equal(GetAttendanceRoster.StatusRecordedElsewhere, row.Status);
        Assert.Null(row.Attendance);
    }

    /// <summary>
    ///     Asserted on the <b>serialised</b> row: a nulled-out object that still serialises its keys is
    ///     a disclosure a DTO-level assertion would miss (DEC-15).
    /// </summary>
    [Fact]
    public async Task Handle_WhenRowBelongsToAnotherSchool_AttendanceDetailIsWithheld()
    {
        Guid schoolId = Guid.NewGuid();
        Guid formerSchoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        Guid attendanceId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId, name: "Receiving");
        await SchoolSeed.AddAsync(dbContext, formerSchoolId, name: "Former");
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await AddAttendanceAsync(
            dbContext, studentId, formerSchoolId, RosterDate, attendanceId,
            attendCode: "ZZ", attendCodeDescription: "Suspended — safeguarding",
            notes: "Health detail the receiving school must not read.", minutesLate: 45);

        PagedResponse<GetAttendanceRoster.Response> result = await Handle(
            dbContext, Query(schoolId), FakeCurrentUser.ScopedTo(schoolId));

        string json = JsonSerializer.Serialize(Assert.Single(result.Items), WebOptions);

        // Structural, not a substring sweep: the row's key set is asserted whole, so a field added to
        // AttendanceDetail later cannot leak through a probe nobody updated. (A bare
        // Assert.DoesNotContain("45", …) also matches a random Guid, which is a test that fails on
        // whichever seed the run happens to draw.)
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(
            ["studentId", "lastName", "firstName", "grade", "status"],
            document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            GetAttendanceRoster.StatusRecordedElsewhere,
            document.RootElement.GetProperty("status").GetString());

        Assert.DoesNotContain("attendance", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("notes", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("minutesLate", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ZZ", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Suspended", json, StringComparison.Ordinal);
        Assert.DoesNotContain("safeguarding", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(attendanceId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(formerSchoolId.ToString(), json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The negative control. It fails if someone adds <c>&amp;&amp; a.SchoolId == schoolId</c> to the
    ///     <b>join</b> instead of to the projection — the change that turns criterion 5 back into
    ///     criterion 3.
    /// </summary>
    [Fact]
    public async Task Handle_WhenRowBelongsToThisSchool_StatusIsRecorded()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await AddAttendanceAsync(dbContext, studentId, schoolId, RosterDate);

        PagedResponse<GetAttendanceRoster.Response> result = await Handle(
            dbContext, Query(schoolId), FakeCurrentUser.ScopedTo(schoolId));

        GetAttendanceRoster.Response row = Assert.Single(result.Items);
        Assert.Equal(GetAttendanceRoster.StatusRecorded, row.Status);
        Assert.NotNull(row.Attendance);
    }

    // ------------------------------------------------------------------- V-21's coalesce

    [Fact]
    public async Task Handle_WhenNeverModified_ProjectsLastUpdatedFromCreatedAt()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        StudentAttendance attendance = await AddAttendanceAsync(dbContext, studentId, schoolId, RosterDate);

        clock.Advance(TimeSpan.FromHours(3));

        PagedResponse<GetAttendanceRoster.Response> result = await Handle(
            dbContext, Query(schoolId), FakeCurrentUser.ScopedTo(schoolId));

        GetAttendanceRoster.Response.AttendanceDetail detail =
            Assert.Single(result.Items).Attendance ?? throw new InvalidOperationException("no attendance");
        Assert.Null(attendance.ModifiedAt);
        Assert.Equal(attendance.CreatedAt, detail.LastUpdatedAt);
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
        StudentAttendance attendance = await AddAttendanceAsync(dbContext, studentId, schoolId, RosterDate);

        clock.Advance(TimeSpan.FromHours(3));
        attendance.MinutesLate = 5;
        await dbContext.SaveChangesAsync(CancellationToken.None);

        PagedResponse<GetAttendanceRoster.Response> result = await Handle(
            dbContext, Query(schoolId), FakeCurrentUser.ScopedTo(schoolId));

        GetAttendanceRoster.Response.AttendanceDetail detail =
            Assert.Single(result.Items).Attendance ?? throw new InvalidOperationException("no attendance");
        Assert.NotNull(attendance.ModifiedAt);
        Assert.NotEqual(attendance.CreatedAt, detail.LastUpdatedAt);
        Assert.Equal(attendance.ModifiedAt, detail.LastUpdatedAt);
    }

    // ------------------------------------------------------------- authorisation (T06-06)

    [Fact]
    public async Task Handle_WhenSchoolOutsideScope_ThrowsNotFound()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId);

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, Query(schoolId), FakeCurrentUser.ScopedTo(Guid.NewGuid())));

        Assert.Equal(ErrorCodes.School.NotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WhenSchoolDoesNotExist_ThrowsNotFound()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, Query(Guid.NewGuid()), FakeCurrentUser.SystemAdmin()));

        Assert.Equal(ErrorCodes.School.NotFound, exception.ErrorCode);
    }

    /// <summary>
    ///     Holds by construction today because <c>NotFoundException</c> takes no message parameter; this
    ///     is what fails when someone adds an overload.
    /// </summary>
    [Fact]
    public async Task Handle_WhenSchoolOutsideScopeAndWhenAbsent_ProduceIdenticalPayloads()
    {
        Guid existsButUnauthorized = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, existsButUnauthorized);

        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(Guid.NewGuid());

        NotFoundException crossTenant = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, Query(existsButUnauthorized), caller));
        NotFoundException absent = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, Query(Guid.NewGuid()), caller));

        Assert.Equal(absent.ErrorCode, crossTenant.ErrorCode);
        Assert.Equal(absent.Message, crossTenant.Message);
    }

    [Fact]
    public async Task Handle_WhenSystemAdmin_ReadsAnySchool()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        PagedResponse<GetAttendanceRoster.Response> result = await Handle(
            dbContext, Query(schoolId), FakeCurrentUser.SystemAdmin());

        Assert.Equal(studentId, Assert.Single(result.Items).StudentId);
    }

    /// <summary>200, not 409. Refusing to <i>submit</i> to an inactive school is V-14 and is F07's.</summary>
    [Fact]
    public async Task Handle_WhenSchoolInactive_ReturnsRoster()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId, isActive: false);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        PagedResponse<GetAttendanceRoster.Response> result = await Handle(
            dbContext, Query(schoolId), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Equal(studentId, Assert.Single(result.Items).StudentId);
    }

    // -------------------------------------------------------------------------- helpers

    internal static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    internal static readonly DateOnly RosterDate = new(2026, 9, 14);

    private const string DefaultCode = "A";

    private const string DefaultCodeDescription = "Absent — unexcused";

    /// <summary>
    ///     Seeds one school with grades <c>09</c>, <c>10</c> and <c>K</c>, plus a null-grade student —
    ///     the fixture both the F06 grade tests and the F05 parity tests drive.
    /// </summary>
    /// <remarks>
    ///     <c>K</c> earns its place: every other grade in the fixture is digits, and digits have no
    ///     case, so a fixture without a letter grade cannot distinguish an ordinal match from a
    ///     case-insensitive one. The parity theory drives <c>?grade=k</c> against it.
    /// </remarks>
    internal const int SeededGradeCount = 4;

    internal static async Task<Guid> SeedGradesAsync(SparkrockRwcDbContext dbContext)
    {
        Guid schoolId = Guid.NewGuid();

        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, lastName: "Nine", grade: "09");
        await StudentSeed.AddAsync(dbContext, schoolId, lastName: "Ten", grade: "10");
        await StudentSeed.AddAsync(dbContext, schoolId, lastName: "Kinder", grade: "K");
        await StudentSeed.AddAsync(dbContext, schoolId, lastName: "None", grade: null);

        return schoolId;
    }

    /// <summary>
    ///     Inserts a <see cref="StudentAttendance" /> through the real context, so the audit interceptor
    ///     stamps it. Audit fields are never hand-set (DEC-21).
    /// </summary>
    private static async Task<StudentAttendance> AddAttendanceAsync(
        SparkrockRwcDbContext dbContext,
        Guid studentId,
        Guid schoolId,
        DateOnly attendDate,
        Guid? id = null,
        Guid? attendanceCodeId = null,
        string attendCode = DefaultCode,
        string attendCodeDescription = DefaultCodeDescription,
        bool isAbsent = true,
        bool isExcused = false,
        int? minutesLate = null,
        string? notes = null,
        Guid? termId = null)
    {
        StudentAttendance attendance = new()
        {
            Id = id ?? Guid.NewGuid(),
            StudentId = studentId,
            SchoolId = schoolId,
            AttendDate = attendDate,
            AttendanceCodeId = attendanceCodeId ?? Guid.NewGuid(),
            AttendCode = attendCode,
            AttendCodeDescription = attendCodeDescription,
            IsAbsent = isAbsent,
            IsExcused = isExcused,
            MinutesLate = minutesLate,
            Notes = notes,
            TermId = termId
        };

        dbContext.StudentAttendances.Add(attendance);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        return attendance;
    }

    internal static GetAttendanceRoster.Query Query(
        Guid schoolId,
        string? grade = null,
        int? page = null,
        int? pageSize = null) => new()
    {
        SchoolId = schoolId,
        Date = RosterDate.ToString(GetAttendanceRoster.DateFormat, CultureInfo.InvariantCulture),
        Grade = grade,
        Page = page,
        PageSize = pageSize
    };

    internal static Task<PagedResponse<GetAttendanceRoster.Response>> Handle(
        SparkrockRwcDbContext dbContext,
        GetAttendanceRoster.Query query,
        FakeCurrentUser currentUser)
    {
        GetAttendanceRoster.QueryHandler handler = new(dbContext, currentUser);

        return handler.Handle(query, CancellationToken.None);
    }
}

/// <summary>
///     <b>V-24 is implemented twice</b> — once in <c>GetStudents.QueryHandler</c> (F05) and once in
///     <c>GetAttendanceRoster.QueryHandler</c> (F06) — over two different queries, with no shared
///     function.
/// </summary>
/// <remarks>
///     F05's author flagged that nothing mechanically enforced the parity, and the two rules were free
///     to drift with every test in both features still green. Extracting a shared predicate would mean
///     editing <c>src/domain/</c> or F05's slice, which conventions §3 points at
///     (<c>domain/&lt;Aggregate&gt;/</c>) but which is outside F06's edit surface.
///     <para>
///         This class is the mechanism instead: it drives <b>both handlers</b> over <b>one</b> seeded
///         school and asserts they select the same students for the same <c>?grade=</c>. It fails if
///         either implementation changes its trimming, its case sensitivity, its null-grade handling or
///         its blank-means-all rule — including if F05 is the one that changes. It is not as good as
///         one function; it is the strongest guard available from here.
///     </para>
/// </remarks>
public sealed class GetAttendanceRosterGradeFilterParityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("09")]
    [InlineData(" 09 ")]
    [InlineData("9")]
    [InlineData("099")]
    [InlineData("K")]
    [InlineData("k")]
    [InlineData("Kinder")]
    public async Task Handle_SelectsTheSameStudentsAsTheStudentList(string? grade)
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        Guid schoolId = await GetAttendanceRosterHandlerTests.SeedGradesAsync(dbContext);
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        PagedResponse<GetAttendanceRoster.Response> roster = await GetAttendanceRosterHandlerTests.Handle(
            dbContext, GetAttendanceRosterHandlerTests.Query(schoolId, grade), caller);

        GetStudents.QueryHandler studentList = new(dbContext, caller);
        PagedResponse<GetStudentById.Response> students = await studentList.Handle(
            new GetStudents.Query { SchoolId = schoolId, Grade = grade },
            CancellationToken.None);

        Assert.Equal(
            students.Items.Select(student => student.Id).Order().ToArray(),
            roster.Items.Select(row => row.StudentId).Order().ToArray());
        Assert.Equal(students.Page.TotalItems, roster.Page.TotalItems);
    }

    /// <summary>
    ///     A guard on the guard: a fixture where every case selected the same set would make the theory
    ///     above pass whatever either handler did. This pins that the seed actually discriminates.
    /// </summary>
    [Fact]
    public async Task Handle_TheParityFixtureDistinguishesTheCases()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        Guid schoolId = await GetAttendanceRosterHandlerTests.SeedGradesAsync(dbContext);
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        PagedResponse<GetAttendanceRoster.Response> all = await GetAttendanceRosterHandlerTests.Handle(
            dbContext, GetAttendanceRosterHandlerTests.Query(schoolId), caller);
        PagedResponse<GetAttendanceRoster.Response> nine = await GetAttendanceRosterHandlerTests.Handle(
            dbContext, GetAttendanceRosterHandlerTests.Query(schoolId, "09"), caller);
        PagedResponse<GetAttendanceRoster.Response> nothing = await GetAttendanceRosterHandlerTests.Handle(
            dbContext, GetAttendanceRosterHandlerTests.Query(schoolId, "9"), caller);

        Assert.Equal(GetAttendanceRosterHandlerTests.SeededGradeCount, all.Page.TotalItems);
        Assert.Equal(1, nine.Page.TotalItems);
        Assert.Equal(0, nothing.Page.TotalItems);
    }
}

/// <summary>
///     Where <c>UseSparkrockRwc</c> mounts this slice.
/// </summary>
/// <remarks>
///     The walk supplies the <c>features</c> assembly explicitly, for the reason
///     <c>Routing/RouteGroupTests</c> records: production discovery keys on
///     <c>Assembly.GetEntryAssembly()</c>, which under a test runner is the test host.
/// </remarks>
public sealed class GetAttendanceRosterEndpointTests
{
    private const string ExpectedPath = "api/v1/schools/{schoolId}/attendance/{date}";

    [Fact]
    public void AddRoutes_MapsThePathOnceUnderTheVersionedGroup()
    {
        Assert.Single(MappedRoutes(), route =>
            string.Equals(route.RoutePattern.RawText, ExpectedPath, StringComparison.Ordinal));
    }

    /// <summary>
    ///     A <c>:datetime</c> constraint on <c>{date}</c> would turn <c>2026-13-01</c> into a routing
    ///     404 with <c>SYSTEM.NOT_FOUND</c>, indistinguishable from an unknown school — the outcome
    ///     spec §6 exists to prevent. The route-value key must also stay <c>date</c>, or
    ///     <c>ViolationSource</c> reports a path failure as a query one.
    /// </summary>
    [Fact]
    public void AddRoutes_ConstrainsNeitherRouteValue()
    {
        RouteEndpoint route = Route();

        Assert.Equal(["schoolId", "date"], route.RoutePattern.Parameters.Select(parameter => parameter.Name));
        Assert.All(route.RoutePattern.Parameters, parameter => Assert.Empty(parameter.ParameterPolicies));
    }

    [Fact]
    public void AddRoutes_DeclaresTheNameAndTag()
    {
        RouteEndpoint route = Route();

        Assert.Equal(
            nameof(GetAttendanceRoster),
            route.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName);
        Assert.Equal("Attendance", Assert.Single(route.Metadata.GetMetadata<ITagsMetadata>()?.Tags ?? []));
    }

    /// <summary>One <c>.ProducesProblem</c> per documented failure status (spec §7).</summary>
    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(404)]
    public void AddRoutes_DocumentsTheStatus(int status)
    {
        Assert.Contains(
            Route().Metadata.OfType<IProducesResponseTypeMetadata>(),
            metadata => metadata.StatusCode == status);
    }

    private static RouteEndpoint Route() =>
        Assert.Single(MappedRoutes(), route =>
            string.Equals(route.RoutePattern.RawText, ExpectedPath, StringComparison.Ordinal));

    private static RouteEndpoint[] MappedRoutes()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        builder.Services.AddMediatR(configuration =>
            configuration.RegisterServicesFromAssembly(typeof(features.ServiceExtensions).Assembly));
        builder.Services.AddCarter(
            new DependencyContextAssemblyCatalog([typeof(features.ServiceExtensions).Assembly]));

        WebApplication app = builder.Build();
        app.UseSparkrockRwc();

        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
    }
}
