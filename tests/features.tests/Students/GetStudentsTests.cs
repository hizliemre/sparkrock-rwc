using domain.Exceptions;
using features.Paging;
using features.Students;
using features.tests.Fakes;
using features.tests.Schools;
using FluentValidation.Results;
using infra.persistence.postgre;

namespace features.tests.Students;

public sealed class GetStudentsValidatorTests
{
    private readonly GetStudents.QueryValidator _validator = new();

    [Fact]
    public void Validate_WhenPageSizeExceedsMaximum_Fails()
    {
        GetStudents.Query query = Query(pageSize: PagingRules.MaxPageSize + 1);

        ValidationResult result = _validator.Validate(query);

        Assert.False(result.IsValid);
        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(GetStudents.Query.PageSize), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.PageSizeExceeded, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenPageIsZero_Fails()
    {
        ValidationResult result = _validator.Validate(Query(page: 0));

        Assert.False(result.IsValid);
        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(GetStudents.Query.Page), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
    }

    /// <summary>Matches the column width, so the filter cannot be longer than anything it can match.</summary>
    [Fact]
    public void Validate_WhenGradeFilterExceeds10_Fails()
    {
        ValidationResult result = _validator.Validate(
            Query(grade: new string('9', CreateStudent.MaxGradeLength + 1)));

        Assert.False(result.IsValid);
        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(GetStudents.Query.Grade), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenEveryOptionalParameterIsAbsent_Succeeds()
    {
        ValidationResult result = _validator.Validate(Query());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    /// <summary>D-06's corrected reading: an empty <c>?grade=</c> is not a filter, so it is not a failure.</summary>
    [Fact]
    public void Validate_WhenGradeFilterIsEmpty_Succeeds()
    {
        ValidationResult result = _validator.Validate(Query(grade: string.Empty));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    private static GetStudents.Query Query(int? page = null, int? pageSize = null, string? grade = null) => new()
    {
        SchoolId = Guid.NewGuid(),
        Page = page,
        PageSize = pageSize,
        Grade = grade
    };
}

public sealed class GetStudentsHandlerTests
{
    /// <summary>
    ///     The default sort, and it is <b>total</b>. A non-total order under the global
    ///     <c>SplitQuery</c> setting can repeat a row on one page and drop another (VC-27), so every
    ///     default sort ends in <c>Id</c>. It is also the order a register is read in.
    /// </summary>
    [Fact]
    public async Task Handle_OrdersByLastNameThenFirstNameThenId()
    {
        Guid schoolId = Guid.NewGuid();
        Guid lowerId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Guid higherId = Guid.Parse("00000000-0000-0000-0000-000000000003");
        Guid bobId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        Guid youngId = Guid.Parse("00000000-0000-0000-0000-0000000000ff");

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);

        // Inserted against every ordering at once: the alphabetically-last surname first, the
        // first-name tie-break out of order, and the id tie-break with the higher id inserted first.
        await StudentSeed.AddAsync(dbContext, schoolId, youngId, firstName: "Zoe", lastName: "Young");
        await StudentSeed.AddAsync(dbContext, schoolId, bobId, firstName: "Bob", lastName: "Adams");
        await StudentSeed.AddAsync(dbContext, schoolId, higherId, firstName: "Anna", lastName: "Adams");
        await StudentSeed.AddAsync(dbContext, schoolId, lowerId, firstName: "Anna", lastName: "Adams");

        PagedResponse<GetStudentById.Response> result = await Handle(
            dbContext, Query(schoolId), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Equal(
            [lowerId, higherId, bobId, youngId],
            result.Items.Select(student => student.Id));
    }

    [Fact]
    public async Task Handle_ByDefaultExcludesInactiveStudents()
    {
        Guid schoolId = Guid.NewGuid();
        Guid activeId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, activeId, lastName: "Active");
        await StudentSeed.AddAsync(dbContext, schoolId, lastName: "Withdrawn", isActive: false);

        PagedResponse<GetStudentById.Response> result = await Handle(
            dbContext, Query(schoolId), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Equal(activeId, Assert.Single(result.Items).Id);
        Assert.Equal(1, result.Page.TotalItems);
    }

    [Fact]
    public async Task Handle_WhenIncludeInactive_ReturnsBoth()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, lastName: "Active");
        await StudentSeed.AddAsync(dbContext, schoolId, lastName: "Withdrawn", isActive: false);

        PagedResponse<GetStudentById.Response> result = await Handle(
            dbContext,
            Query(schoolId, includeInactive: true),
            FakeCurrentUser.ScopedTo(schoolId));

        Assert.Equal(2, result.Items.Count);
        Assert.Contains(result.Items, student => !student.IsActive);
    }

    [Fact]
    public async Task Handle_WhenGradeGiven_ReturnsOnlyThatGrade()
    {
        Guid schoolId = Guid.NewGuid();
        Guid nineId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, nineId, lastName: "Nine", grade: "09");
        await StudentSeed.AddAsync(dbContext, schoolId, lastName: "Ten", grade: "10");

        PagedResponse<GetStudentById.Response> result = await Handle(
            dbContext, Query(schoolId, grade: "09"), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Equal(nineId, Assert.Single(result.Items).Id);
        Assert.Equal(1, result.Page.TotalItems);
    }

    /// <summary>
    ///     <b>D-06's corrected reading.</b> Legacy's <c>cboGrade.Clear()</c> ran immediately before the
    ///     only read, so the roster procedure was called with <c>''</c> every single time (L-15) and
    ///     "all grades" is the only behaviour anyone ever observed. This is also V-24's rule, which F06
    ///     owns; the two features must not diverge.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_WhenGradeIsEmpty_ReturnsAllGrades(string grade)
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, lastName: "Nine", grade: "09");
        await StudentSeed.AddAsync(dbContext, schoolId, lastName: "Ten", grade: "10");
        await StudentSeed.AddAsync(dbContext, schoolId, lastName: "None", grade: null);

        PagedResponse<GetStudentById.Response> result = await Handle(
            dbContext, Query(schoolId, grade: grade), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Equal(3, result.Page.TotalItems);
    }

    [Fact]
    public async Task Handle_WhenGradeAbsent_ReturnsAllGrades()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, lastName: "Nine", grade: "09");
        await StudentSeed.AddAsync(dbContext, schoolId, lastName: "Ten", grade: "10");
        await StudentSeed.AddAsync(dbContext, schoolId, lastName: "None", grade: null);

        PagedResponse<GetStudentById.Response> result = await Handle(
            dbContext, Query(schoolId), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Equal(3, result.Page.TotalItems);
    }

    /// <summary>
    ///     There is no <c>?grade=none</c> sentinel: a magic string in the value space of a free-text
    ///     column is a bug waiting for a school that names a grade "none".
    /// </summary>
    [Fact]
    public async Task Handle_WhenGradeGiven_ExcludesStudentsWithNoGrade()
    {
        Guid schoolId = Guid.NewGuid();
        Guid nineId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, nineId, lastName: "Nine", grade: "09");
        await StudentSeed.AddAsync(dbContext, schoolId, lastName: "None", grade: null);

        PagedResponse<GetStudentById.Response> result = await Handle(
            dbContext, Query(schoolId, grade: "09"), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Equal(nineId, Assert.Single(result.Items).Id);
    }

    /// <summary>
    ///     Exact match, never prefix or contains. <c>?grade=1</c> under a <c>contains</c> would return
    ///     every student in grades 10, 11 and 12.
    /// </summary>
    [Fact]
    public async Task Handle_WhenGradeGiven_MatchesExactlyNotByPrefix()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, lastName: "Ten", grade: "10");
        await StudentSeed.AddAsync(dbContext, schoolId, lastName: "Eleven", grade: "11");
        await StudentSeed.AddAsync(dbContext, schoolId, lastName: "Twelve", grade: "12");

        PagedResponse<GetStudentById.Response> result = await Handle(
            dbContext, Query(schoolId, grade: "1"), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Empty(result.Items);
        Assert.Equal(0, result.Page.TotalItems);
    }

    /// <summary>A surrounding-whitespace <c>?grade=</c> still names a grade, and is trimmed.</summary>
    [Fact]
    public async Task Handle_WhenGradeIsPadded_TrimsBeforeMatching()
    {
        Guid schoolId = Guid.NewGuid();
        Guid nineId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, nineId, lastName: "Nine", grade: "09");
        await StudentSeed.AddAsync(dbContext, schoolId, lastName: "Ten", grade: "10");

        PagedResponse<GetStudentById.Response> result = await Handle(
            dbContext, Query(schoolId, grade: " 09 "), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Equal(nineId, Assert.Single(result.Items).Id);
    }

    [Fact]
    public async Task Handle_ReturnsOnlyStudentsOfTheAddressedSchool()
    {
        Guid addressedSchoolId = Guid.NewGuid();
        Guid otherSchoolId = Guid.NewGuid();
        Guid mineId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, addressedSchoolId, name: "Addressed");
        await SchoolSeed.AddAsync(dbContext, otherSchoolId, name: "Other");
        await StudentSeed.AddAsync(dbContext, addressedSchoolId, mineId, lastName: "Mine");
        await StudentSeed.AddAsync(dbContext, otherSchoolId, lastName: "Theirs");

        // Authorised for both, so only the SchoolId predicate can keep the other school's roster out.
        PagedResponse<GetStudentById.Response> result = await Handle(
            dbContext,
            Query(addressedSchoolId),
            FakeCurrentUser.ScopedTo(addressedSchoolId, otherSchoolId));

        Assert.Equal(mineId, Assert.Single(result.Items).Id);
        Assert.Equal(1, result.Page.TotalItems);
    }

    /// <summary>
    ///     404, not an empty page: the school is an <c>{id}</c> in the path (conventions §2). The
    ///     caller is an administrator, so the scope check cannot be what produces it.
    /// </summary>
    [Fact]
    public async Task Handle_WhenSchoolDoesNotExist_ThrowsNotFound()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, Query(Guid.NewGuid()), FakeCurrentUser.SystemAdmin()));

        Assert.Equal(ErrorCodes.School.NotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WhenSchoolIsOutsideScope_ThrowsNotFound()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId);

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, Query(schoolId), FakeCurrentUser.ScopedTo(Guid.NewGuid())));

        Assert.Equal(ErrorCodes.School.NotFound, exception.ErrorCode);
    }

    /// <summary>
    ///     A school outside scope and an absent school produce byte-identical 404s (acceptance §5).
    /// </summary>
    [Fact]
    public async Task Handle_WhenSchoolIsOutsideScope_ProducesTheSamePayloadAsAbsentSchool()
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

    /// <summary>The school exists, so this is 200 with <c>[]</c> rather than a 404.</summary>
    [Fact]
    public async Task Handle_WhenSchoolHasNoStudents_ReturnsEmptyItems()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);

        PagedResponse<GetStudentById.Response> result = await Handle(
            dbContext, Query(schoolId), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Empty(result.Items);
        Assert.Equal(0, result.Page.TotalItems);
        Assert.Equal(0, result.Page.TotalPages);
    }

    /// <summary>
    ///     DEC-19: an inactive school still serves its roster. <c>SCHOOL.INACTIVE</c> is F07's.
    /// </summary>
    [Fact]
    public async Task Handle_WhenSchoolIsInactive_StillServesItsRoster()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId, isActive: false);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        PagedResponse<GetStudentById.Response> result = await Handle(
            dbContext, Query(schoolId), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Equal(studentId, Assert.Single(result.Items).Id);
    }

    [Fact]
    public async Task Handle_ReturnsTheCollectionEnvelope()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        for (int index = 0; index < 3; index++)
        {
            await StudentSeed.AddAsync(dbContext, schoolId, lastName: FormattableString.Invariant($"Student{index}"));
        }

        PagedResponse<GetStudentById.Response> result = await Handle(
            dbContext,
            Query(schoolId, page: 2, pageSize: 2),
            FakeCurrentUser.ScopedTo(schoolId));

        Assert.Single(result.Items);
        Assert.Equal(2, result.Page.Number);
        Assert.Equal(2, result.Page.Size);
        Assert.Equal(3, result.Page.TotalItems);
        Assert.Equal(2, result.Page.TotalPages);
    }

    /// <summary>
    ///     A page past the end is an empty page, not a 404 and not a provider-level failure.
    /// </summary>
    [Fact]
    public async Task Handle_WhenPageIsPastTheEnd_ReturnsEmptyItemsWithTheTotals()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId);

        PagedResponse<GetStudentById.Response> result = await Handle(
            dbContext,
            Query(schoolId, page: 500, pageSize: 10),
            FakeCurrentUser.ScopedTo(schoolId));

        Assert.Empty(result.Items);
        Assert.Equal(500, result.Page.Number);
        Assert.Equal(1, result.Page.TotalItems);
        Assert.Equal(1, result.Page.TotalPages);
    }

    /// <summary>
    ///     An absent <c>?pageSize=</c> resolves to <see cref="PagingRules.DefaultPageSize" />, never to
    ///     "everything".
    /// </summary>
    [Fact]
    public async Task Handle_WhenPageSizeIsAbsent_AppliesTheDefault()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);

        PagedResponse<GetStudentById.Response> result = await Handle(
            dbContext, Query(schoolId), FakeCurrentUser.ScopedTo(schoolId));

        Assert.Equal(PagingRules.DefaultPage, result.Page.Number);
        Assert.Equal(PagingRules.DefaultPageSize, result.Page.Size);
    }

    private static GetStudents.Query Query(
        Guid schoolId,
        int? page = null,
        int? pageSize = null,
        string? grade = null,
        bool includeInactive = false) => new()
    {
        SchoolId = schoolId,
        Page = page,
        PageSize = pageSize,
        Grade = grade,
        IncludeInactive = includeInactive
    };

    private static Task<PagedResponse<GetStudentById.Response>> Handle(
        SparkrockRwcDbContext dbContext,
        GetStudents.Query query,
        FakeCurrentUser currentUser)
    {
        GetStudents.QueryHandler handler = new(dbContext, currentUser);

        return handler.Handle(query, CancellationToken.None);
    }
}
