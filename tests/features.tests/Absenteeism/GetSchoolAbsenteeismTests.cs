using System.Globalization;
using System.Text.Json;
using domain.Attendance;
using domain.Exceptions;
using domain.Security;
using features.Absenteeism;
using features.Paging;
using features.tests.Fakes;
using features.tests.Schools;
using features.tests.Students;
using FluentValidation.Results;
using infra.persistence.postgre;
using Microsoft.Extensions.Time.Testing;

namespace features.tests.Absenteeism;

public sealed class GetSchoolAbsenteeismValidatorTests
{
    private static readonly GetSchoolAbsenteeism.QueryValidator Validator = new();

    [Fact]
    public void Validate_WhenNothingSupplied_Succeeds()
    {
        ValidationResult result = Validator.Validate(Query());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WhenPageSizeExceedsMaximum_Fails()
    {
        ValidationResult result = Validator.Validate(Query(pageSize: PagingRules.MaxPageSize + 1));

        Assert.False(result.IsValid);
        Assert.Equal(ErrorCodes.Validation.PageSizeExceeded, Assert.Single(result.Errors).ErrorCode);
    }

    [Fact]
    public void Validate_WhenPageBelowOne_Fails()
    {
        ValidationResult result = Validator.Validate(Query(page: 0));

        Assert.False(result.IsValid);
        Assert.Equal(ErrorCodes.Validation.Failed, Assert.Single(result.Errors).ErrorCode);
    }

    [Fact]
    public void Validate_WhenSchoolYearBelowMinimum_Fails()
    {
        ValidationResult result =
            Validator.Validate(Query(schoolYear: domain.ValueObjects.SchoolYear.MinStartYear - 1));

        Assert.False(result.IsValid);
        Assert.Equal(ErrorCodes.Validation.Failed, Assert.Single(result.Errors).ErrorCode);
    }

    [Fact]
    public void Validate_WhenSchoolYearAboveMaximum_Fails()
    {
        ValidationResult result =
            Validator.Validate(Query(schoolYear: domain.ValueObjects.SchoolYear.MaxStartYear + 1));

        Assert.False(result.IsValid);
        Assert.Equal(ErrorCodes.Validation.Failed, Assert.Single(result.Errors).ErrorCode);
    }

    private static GetSchoolAbsenteeism.Query Query(int? schoolYear = null, int? page = null, int? pageSize = null) =>
        new() { SchoolId = Guid.NewGuid(), SchoolYear = schoolYear, Page = page, PageSize = pageSize };
}

public sealed class GetSchoolAbsenteeismHandlerTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     Rows are selected by <c>Student.SchoolId</c>, not by the summary's school of record. The
    ///     summary seeded here for the other school's student is what a legacy-shaped join would pull in.
    /// </summary>
    [Fact]
    public async Task Handle_ListsStudentsOfTheSchool()
    {
        Guid schoolId = Guid.NewGuid();
        Guid otherSchoolId = Guid.NewGuid();
        Guid mine = Guid.NewGuid();
        Guid theirs = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId, name: "Path School");
        await SchoolSeed.AddAsync(dbContext, otherSchoolId, name: "Other School");
        await StudentSeed.AddAsync(dbContext, schoolId, mine, lastName: "Mine");
        await StudentSeed.AddAsync(dbContext, otherSchoolId, theirs, lastName: "Theirs");
        await AbsenteeismSeed.SummaryAsync(dbContext, theirs, schoolId, 2026, 30);

        PagedResponse<GetSchoolAbsenteeism.Response> page = await Handle(dbContext, schoolId, 2026);

        Assert.Equal(mine, Assert.Single(page.Items).StudentId);
        Assert.Equal(1, page.Page.TotalItems);
    }

    [Fact]
    public async Task Handle_WhenStudentHasNoSummary_ProjectsZeroAbsences()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        PagedResponse<GetSchoolAbsenteeism.Response> page = await Handle(dbContext, schoolId, 2026);

        GetSchoolAbsenteeism.Response row = Assert.Single(page.Items);

        Assert.Equal(0, row.TotalAbsences);
        Assert.False(row.IsChronicallyAbsent);
        Assert.Null(row.LastUpdatedAt);
    }

    [Fact]
    public async Task Handle_OrdersByTotalAbsencesDescending()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);

        Guid low = await StudentWithAbsencesAsync(dbContext, schoolId, "Aaa", 1);
        Guid high = await StudentWithAbsencesAsync(dbContext, schoolId, "Zzz", 25);
        Guid middle = await StudentWithAbsencesAsync(dbContext, schoolId, "Mmm", 9);

        PagedResponse<GetSchoolAbsenteeism.Response> page = await Handle(dbContext, schoolId, 2026);

        Assert.Equal([high, middle, low], page.Items.Select(row => row.StudentId).ToArray());
    }

    /// <summary>
    ///     The <c>NULLS FIRST</c> trap. A left join leaves <c>TotalAbsences</c> null for a student
    ///     with no summary, and Postgres sorts nulls first under <c>ORDER BY … DESC</c> — putting the
    ///     students with no absences at the top of a worst-first worklist. The fix is to order over
    ///     the coalesced projection.
    /// </summary>
    /// <remarks>
    ///     InMemory does not reproduce Postgres's null ordering, so this pins the <i>expression</i>
    ///     rather than the provider behaviour. The SQL-shape half is asserted at the integration tier.
    /// </remarks>
    [Fact]
    public async Task Handle_WhenStudentHasNoSummary_SortsToTheBottom()
    {
        Guid schoolId = Guid.NewGuid();
        Guid withoutSummary = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, withoutSummary, lastName: "Aaa");

        Guid withOne = await StudentWithAbsencesAsync(dbContext, schoolId, "Zzz", 1);

        PagedResponse<GetSchoolAbsenteeism.Response> page = await Handle(dbContext, schoolId, 2026);

        Assert.Equal([withOne, withoutSummary], page.Items.Select(row => row.StudentId).ToArray());
    }

    /// <summary>
    ///     A non-total order under the global <c>SplitQuery</c> setting can repeat a row on one page
    ///     and drop another (VC-27), so the sort ends in <c>Id</c>.
    /// </summary>
    [Fact]
    public async Task Handle_OrderIsTotalWithLastNameFirstNameAndId()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);

        Guid[] shared = [Guid.NewGuid(), Guid.NewGuid()];
        Array.Sort(shared);

        Guid ada = await StudentWithAbsencesAsync(dbContext, schoolId, "Okafor", 4, firstName: "Ada");
        Guid boSecond = await StudentWithAbsencesAsync(
            dbContext, schoolId, "Okafor", 4, firstName: "Bo", id: shared[1]);
        Guid boFirst = await StudentWithAbsencesAsync(
            dbContext, schoolId, "Okafor", 4, firstName: "Bo", id: shared[0]);

        PagedResponse<GetSchoolAbsenteeism.Response> page = await Handle(dbContext, schoolId, 2026);

        Assert.Equal([ada, boFirst, boSecond], page.Items.Select(row => row.StudentId).ToArray());
    }

    /// <summary>
    ///     Spec §7.2's deliberate asymmetry: the six absenteeism members are identical in name and
    ///     meaning to the single read's, and <c>schoolYear</c>/<c>schoolYearLabel</c> are not repeated
    ///     per row — the year is in the request.
    /// </summary>
    [Fact]
    public async Task Handle_ProjectsTheSameSixAbsenteeismMembersAsTheSingleRead()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId, threshold: 10);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await AbsenteeismSeed.SummaryAsync(dbContext, studentId, schoolId, 2026, 14);

        PagedResponse<GetSchoolAbsenteeism.Response> page = await Handle(dbContext, schoolId, 2026);
        string json = JsonSerializer.Serialize(Assert.Single(page.Items), WebOptions);

        foreach (string member in new[]
                 {
                     "totalAbsences", "threshold", "thresholdSource", "isChronicallyAbsent",
                     "includesOtherSchoolAbsences", "lastUpdatedAt"
                 })
        {
            Assert.Contains(member, json, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("schoolYear", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("schoolId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("thresholdSourceSchoolId", json, StringComparison.OrdinalIgnoreCase);

        foreach (string banned in new[] { "rate", "percentage", "enrolledDays", "daysPossible" })
            Assert.DoesNotContain(banned, json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     DEC-16 on the list route. The summary's school of record sets 20; the path school — which
    ///     is every listed student's current school — sets 5.
    /// </summary>
    [Fact]
    public async Task Handle_ResolvesThresholdFromThePathSchool()
    {
        Guid schoolId = Guid.NewGuid();
        Guid otherSchoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId, name: "Path School", threshold: 5);
        await SchoolSeed.AddAsync(dbContext, otherSchoolId, name: "Record School", threshold: 20);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await AbsenteeismSeed.SummaryAsync(dbContext, studentId, otherSchoolId, 2026, 10);

        PagedResponse<GetSchoolAbsenteeism.Response> page = await Handle(dbContext, schoolId, 2026);
        GetSchoolAbsenteeism.Response row = Assert.Single(page.Items);

        Assert.Equal(5, row.Threshold);
        Assert.True(row.IsChronicallyAbsent);
        Assert.Equal("currentSchool", row.ThresholdSource);
    }

    [Fact]
    public async Task Handle_WhenChronicOnly_ExcludesNonChronicStudents()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId, threshold: 10);

        Guid chronic = await StudentWithAbsencesAsync(dbContext, schoolId, "Chronic", 10);
        await StudentWithAbsencesAsync(dbContext, schoolId, "Borderline", 9);

        PagedResponse<GetSchoolAbsenteeism.Response> page =
            await Handle(dbContext, schoolId, 2026, chronicOnly: true);

        Assert.Equal(chronic, Assert.Single(page.Items).StudentId);
    }

    /// <summary>
    ///     The paging assertion. Filtering after materialisation gives <c>totalItems == 30</c> and
    ///     pages of varying size.
    /// </summary>
    [Fact]
    public async Task Handle_WhenChronicOnly_TotalItemsCountsOnlyChronicStudents()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId, threshold: 10);

        for (int index = 0; index < 30; index++)
        {
            await StudentWithAbsencesAsync(
                dbContext,
                schoolId,
                index.ToString("D2", CultureInfo.InvariantCulture),
                index < 4 ? 12 : 1);
        }

        PagedResponse<GetSchoolAbsenteeism.Response> page =
            await Handle(dbContext, schoolId, 2026, chronicOnly: true, pageSize: 10);

        Assert.Equal(4, page.Page.TotalItems);
        Assert.Equal(1, page.Page.TotalPages);
        Assert.Equal(4, page.Items.Count);
    }

    [Fact]
    public async Task Handle_WhenChronicOnly_AndSchoolThresholdIsNull_UsesTheDomainDefault()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId, threshold: null);

        Guid atDefault = await StudentWithAbsencesAsync(
            dbContext, schoolId, "AtDefault", AbsenceRules.DefaultThreshold);
        await StudentWithAbsencesAsync(dbContext, schoolId, "Below", AbsenceRules.DefaultThreshold - 1);

        PagedResponse<GetSchoolAbsenteeism.Response> page =
            await Handle(dbContext, schoolId, 2026, chronicOnly: true);

        Assert.Equal(atDefault, Assert.Single(page.Items).StudentId);
        Assert.Equal(AbsenceRules.DefaultThreshold, page.Items[0].Threshold);
    }

    [Fact]
    public async Task Handle_WhenChronicOnlyIsFalse_ReturnsEveryStudent()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId, threshold: 10);
        await StudentWithAbsencesAsync(dbContext, schoolId, "Chronic", 10);
        await StudentWithAbsencesAsync(dbContext, schoolId, "Borderline", 9);

        PagedResponse<GetSchoolAbsenteeism.Response> page = await Handle(dbContext, schoolId, 2026);

        Assert.Equal(2, page.Page.TotalItems);
    }

    [Fact]
    public async Task Handle_ByDefaultExcludesInactiveStudents()
    {
        Guid schoolId = Guid.NewGuid();
        Guid active = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, active, lastName: "Active");
        await StudentSeed.AddAsync(dbContext, schoolId, Guid.NewGuid(), lastName: "Gone", isActive: false);

        PagedResponse<GetSchoolAbsenteeism.Response> page = await Handle(dbContext, schoolId, 2026);

        Assert.Equal(active, Assert.Single(page.Items).StudentId);
    }

    /// <summary>
    ///     DEC-19. A deactivated student with 20 absences is exactly who a safeguarding worklist must
    ///     not lose.
    /// </summary>
    [Fact]
    public async Task Handle_WhenIncludeInactive_IncludesThem()
    {
        Guid schoolId = Guid.NewGuid();
        Guid gone = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, Guid.NewGuid(), lastName: "Active");
        await StudentSeed.AddAsync(dbContext, schoolId, gone, lastName: "Gone", isActive: false);
        await AbsenteeismSeed.SummaryAsync(dbContext, gone, schoolId, 2026, 20);

        PagedResponse<GetSchoolAbsenteeism.Response> page =
            await Handle(dbContext, schoolId, 2026, includeInactive: true);

        Assert.Equal(2, page.Page.TotalItems);
        Assert.Equal(gone, page.Items[0].StudentId);
    }

    [Fact]
    public async Task Handle_MarkerIsSetPerRow()
    {
        Guid schoolId = Guid.NewGuid();
        Guid otherSchoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId, name: "Path School");
        await SchoolSeed.AddAsync(dbContext, otherSchoolId, name: "Elsewhere Academy");

        Guid travelled = await StudentWithAbsencesAsync(dbContext, schoolId, "Travelled", 5);
        Guid stayed = await StudentWithAbsencesAsync(dbContext, schoolId, "Stayed", 4);

        await AbsenteeismSeed.AttendanceAsync(dbContext, travelled, otherSchoolId, new DateOnly(2026, 10, 5));
        await AbsenteeismSeed.AttendanceAsync(dbContext, stayed, schoolId, new DateOnly(2026, 10, 6));

        PagedResponse<GetSchoolAbsenteeism.Response> page = await Handle(dbContext, schoolId, 2026);

        Assert.Equal(travelled, page.Items[0].StudentId);
        Assert.True(page.Items[0].IncludesOtherSchoolAbsences);
        Assert.Equal(stayed, page.Items[1].StudentId);
        Assert.False(page.Items[1].IncludesOtherSchoolAbsences);

        string json = JsonSerializer.Serialize(page.Items[0], WebOptions);

        Assert.DoesNotContain(otherSchoolId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Elsewhere", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The guard against computing the marker before paging (an unbounded read) or per row (N
    ///     round trips under the global <c>SplitQuery</c>, VC-27).
    /// </summary>
    [Fact]
    public async Task Handle_MarkerQueryCoversOnlyThePage()
    {
        Guid schoolId = Guid.NewGuid();
        Guid otherSchoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId, name: "Path School");
        await SchoolSeed.AddAsync(dbContext, otherSchoolId, name: "Elsewhere Academy");

        List<Guid> byRank = [];

        // Rank i (0-based) carries 60 - i absences, so the ordering is fully determined and page 2
        // holds ranks 20..39.
        for (int index = 0; index < 60; index++)
        {
            byRank.Add(await StudentWithAbsencesAsync(
                dbContext,
                schoolId,
                index.ToString("D2", CultureInfo.InvariantCulture),
                60 - index));
        }

        Guid onPageOne = byRank[3];
        Guid onPageTwo = byRank[25];

        await AbsenteeismSeed.AttendanceAsync(dbContext, onPageOne, otherSchoolId, new DateOnly(2026, 10, 5));
        await AbsenteeismSeed.AttendanceAsync(dbContext, onPageTwo, otherSchoolId, new DateOnly(2026, 10, 6));

        PagedResponse<GetSchoolAbsenteeism.Response> page =
            await Handle(dbContext, schoolId, 2026, page: 2, pageSize: 20);

        Assert.Equal(20, page.Items.Count);
        Assert.Equal(byRank[20], page.Items[0].StudentId);

        foreach (GetSchoolAbsenteeism.Response row in page.Items)
            Assert.Equal(row.StudentId == onPageTwo, row.IncludesOtherSchoolAbsences);
    }

    [Fact]
    public async Task Handle_WhenSchoolOutsideScope_ThrowsNotFound()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, schoolId, 2026, currentUser: FakeCurrentUser.ScopedTo(Guid.NewGuid())));

        Assert.Equal(ErrorCodes.School.NotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WhenSchoolDoesNotExist_ThrowsNotFound()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, Guid.NewGuid(), 2026, currentUser: FakeCurrentUser.SystemAdmin()));

        Assert.Equal(ErrorCodes.School.NotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WhenSchoolOutsideScopeAndWhenAbsent_ProduceIdenticalPayloads()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);

        NotFoundException outOfScope = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, schoolId, 2026, currentUser: FakeCurrentUser.ScopedTo(Guid.NewGuid())));

        NotFoundException absent = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, Guid.NewGuid(), 2026, currentUser: FakeCurrentUser.SystemAdmin()));

        Assert.Equal(absent.ErrorCode, outOfScope.ErrorCode);
        Assert.Equal(absent.Message, outOfScope.Message);
    }

    /// <summary>DEC-19: an inactive school still serves its worklist.</summary>
    [Fact]
    public async Task Handle_WhenSchoolInactive_ReturnsList()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId, isActive: false);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        PagedResponse<GetSchoolAbsenteeism.Response> page = await Handle(dbContext, schoolId, 2026);

        Assert.Equal(studentId, Assert.Single(page.Items).StudentId);
    }

    /// <summary>DEC-12, on the list route: an absent <c>?schoolYear=</c> is school-local today's year.</summary>
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

        PagedResponse<GetSchoolAbsenteeism.Response> page = await Handle(dbContext, schoolId, null, clock: clock);

        Assert.Equal(7, Assert.Single(page.Items).TotalAbsences);
    }

    private static async Task<Guid> StudentWithAbsencesAsync(
        SparkrockRwcDbContext dbContext,
        Guid schoolId,
        string lastName,
        int totalAbsences,
        string firstName = StudentSeed.DefaultFirstName,
        Guid? id = null)
    {
        Guid studentId = id ?? Guid.NewGuid();

        await StudentSeed.AddAsync(dbContext, schoolId, studentId, firstName, lastName);
        await AbsenteeismSeed.SummaryAsync(dbContext, studentId, schoolId, 2026, totalAbsences);

        return studentId;
    }

    private static Task<PagedResponse<GetSchoolAbsenteeism.Response>> Handle(
        SparkrockRwcDbContext dbContext,
        Guid schoolId,
        int? schoolYear,
        bool chronicOnly = false,
        bool includeInactive = false,
        int? page = null,
        int? pageSize = null,
        ICurrentUser? currentUser = null,
        TimeProvider? clock = null)
    {
        GetSchoolAbsenteeism.QueryHandler handler = new(
            dbContext,
            currentUser ?? FakeCurrentUser.ScopedTo(schoolId),
            clock ?? new FakeTimeProvider(InMemoryDbContextFactory.DefaultNow));

        return handler.Handle(
            new GetSchoolAbsenteeism.Query
            {
                SchoolId = schoolId,
                SchoolYear = schoolYear,
                ChronicOnly = chronicOnly,
                IncludeInactive = includeInactive,
                Page = page,
                PageSize = pageSize
            },
            CancellationToken.None);
    }
}
