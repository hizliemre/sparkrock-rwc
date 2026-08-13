using System.Reflection;
using domain.Alerts;
using domain.Attendance;
using domain.Exceptions;
using domain.Schools;
using domain.Security;
using domain.Students;
using features.Alerts;
using features.Paging;
using features.tests.Fakes;
using features.tests.Schools;
using features.tests.Students;
using FluentValidation.Results;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace features.tests.Alerts;

public sealed class GetSchoolAlertsValidatorTests
{
    private static readonly GetSchoolAlerts.QueryValidator Validator = new();

    [Fact]
    public void Validate_WhenPagingIsAbsent_Succeeds()
    {
        Assert.True(Validator.Validate(Query()).IsValid);
    }

    [Fact]
    public void Validate_WhenPageSizeExceedsMax_Fails()
    {
        ValidationResult result = Validator.Validate(Query(pageSize: PagingRules.MaxPageSize + 1));

        Assert.False(result.IsValid);
        Assert.Equal(ErrorCodes.Validation.PageSizeExceeded, Assert.Single(result.Errors).ErrorCode);

        // The property name is the query key, so api/Errors/ViolationSource infers "query" rather
        // than falling back to "body" on a GET that carried none (O-45).
        Assert.Equal(nameof(GetSchoolAlerts.Query.PageSize), Assert.Single(result.Errors).PropertyName);
    }

    [Fact]
    public void Validate_WhenPageBelowOne_Fails()
    {
        ValidationResult result = Validator.Validate(Query(page: 0));

        Assert.False(result.IsValid);
        Assert.Equal(ErrorCodes.Validation.Failed, Assert.Single(result.Errors).ErrorCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("open")]
    [InlineData("resolved")]
    public void Validate_WhenStatusIsRecognised_Succeeds(string? status)
    {
        Assert.True(Validator.Validate(Query(status: status)).IsValid);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("Open ")]
    [InlineData("")]
    public void Validate_WhenStatusIsUnrecognised_Fails(string status)
    {
        ValidationResult result = Validator.Validate(Query(status: status));

        Assert.False(result.IsValid);
        Assert.Equal(ErrorCodes.Validation.Failed, Assert.Single(result.Errors).ErrorCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WhenSchoolYearIsNotPositive_Fails(int schoolYear)
    {
        ValidationResult result = Validator.Validate(Query(schoolYear: schoolYear));

        Assert.False(result.IsValid);
        Assert.Equal(ErrorCodes.Validation.Failed, Assert.Single(result.Errors).ErrorCode);
    }

    /// <summary>
    ///     Spec §5: a closed episode's raise threshold cannot strand anything, so the combination is
    ///     refused rather than silently ignored.
    /// </summary>
    [Fact]
    public void Validate_WhenThresholdDriftRequestedWithResolvedStatus_Fails()
    {
        ValidationResult result = Validator.Validate(Query(status: "resolved", thresholdDrift: true));

        Assert.False(result.IsValid);
        Assert.Equal(ErrorCodes.Validation.Failed, Assert.Single(result.Errors).ErrorCode);
        Assert.Equal(nameof(GetSchoolAlerts.Query.ThresholdDrift), Assert.Single(result.Errors).PropertyName);
    }

    /// <summary>
    ///     The paired positive case (plan R-6). A cross-property rule whose <c>When</c> never
    ///     evaluates true passes the negative test above for the wrong reason.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("open")]
    public void Validate_WhenThresholdDriftRequestedWithOpenStatus_Succeeds(string? status)
    {
        Assert.True(Validator.Validate(Query(status: status, thresholdDrift: true)).IsValid);
    }

    /// <summary>
    ///     <c>?thresholdDrift=false</c> is the default and means "no filter", so it is valid against
    ///     either status. Without this the rule could be written on the property rather than on the
    ///     value and still pass everything above.
    /// </summary>
    [Fact]
    public void Validate_WhenThresholdDriftFalseWithResolvedStatus_Succeeds()
    {
        Assert.True(Validator.Validate(Query(status: "resolved", thresholdDrift: false)).IsValid);
    }

    private static GetSchoolAlerts.Query Query(
        string? status = null,
        int? schoolYear = null,
        bool? thresholdDrift = null,
        int? page = null,
        int? pageSize = null) =>
        new()
        {
            SchoolId = Guid.NewGuid(),
            Status = status,
            SchoolYear = schoolYear,
            ThresholdDrift = thresholdDrift,
            Page = page,
            PageSize = pageSize
        };
}

public sealed class GetSchoolAlertsHandlerTests
{
    [Fact]
    public async Task Handle_WhenNoAlertsExist_ReturnsEmptyEnvelope()
    {
        Guid schoolId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);

        PagedResponse<GetSchoolAlerts.Response> page = await Handle(dbContext, caller, schoolId);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.Page.TotalItems);
    }

    [Fact]
    public async Task Handle_ProjectsTheAlert()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId, threshold: 12);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId, firstName: "Ada", lastName: "Byron");
        StudentAlert alert = await AlertSeed.OpenAsync(
            dbContext, studentId, schoolId, schoolYearStart: 2026, absenceCount: 11, thresholdAtRaise: 10);

        GetSchoolAlerts.Response item = Assert.Single((await Handle(dbContext, caller, schoolId)).Items);

        Assert.Equal(alert.Id, item.Id);
        Assert.Equal(studentId, item.StudentId);
        Assert.Equal("Ada", item.StudentFirstName);
        Assert.Equal("Byron", item.StudentLastName);
        Assert.Equal(nameof(AlertType.ChronicAbsence), item.AlertType);
        Assert.Equal(2026, item.SchoolYear);
        Assert.Equal("2026-2027", item.SchoolYearLabel);
        Assert.Equal(11, item.AbsenceCount);
        Assert.Equal(10, item.ThresholdAtRaise);
        Assert.Equal(12, item.CurrentThreshold);
        Assert.True(item.ThresholdDrift);
        Assert.Equal(GetSchoolAlerts.OpenStatus, item.Status);
        Assert.Equal(InMemoryDbContextFactory.DefaultNow, item.RaisedAt);
    }

    [Fact]
    public async Task Handle_ReturnsOpenAlertsByDefault()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        StudentAlert open = await AlertSeed.OpenAsync(dbContext, studentId, schoolId, schoolYearStart: 2026);
        await AlertSeed.ResolvedAsync(dbContext, studentId, schoolId, schoolYearStart: 2025);

        PagedResponse<GetSchoolAlerts.Response> page = await Handle(dbContext, caller, schoolId);

        Assert.Equal(open.Id, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task Handle_WhenStatusResolved_ReturnsResolvedOnly()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        Guid resolver = Guid.Parse("33333333-3333-3333-3333-333333333333");
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await AlertSeed.OpenAsync(dbContext, studentId, schoolId, schoolYearStart: 2026);
        StudentAlert resolved = await AlertSeed.ResolvedAsync(
            dbContext,
            studentId,
            schoolId,
            schoolYearStart: 2025,
            resolutionReason: "Home visit completed.",
            resolvedBy: resolver);

        GetSchoolAlerts.Response item = Assert.Single(
            (await Handle(dbContext, caller, schoolId, status: "resolved")).Items);

        Assert.Equal(resolved.Id, item.Id);
        Assert.Equal(GetSchoolAlerts.ResolvedStatus, item.Status);
        Assert.Equal(resolved.ResolvedAt, item.ResolvedAt);
        Assert.Equal(resolver, item.ResolvedBy);
        Assert.Equal(nameof(ResolutionSource.Manual), item.ResolutionSource);
        Assert.Equal("Home visit completed.", item.ResolutionReason);
    }

    /// <summary>
    ///     All four resolution members are null on an open episode, so the serializer omits them
    ///     rather than writing four nulls (conventions §2).
    /// </summary>
    [Fact]
    public async Task Handle_WhenStatusOpen_OmitsResolutionFields()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await AlertSeed.OpenAsync(dbContext, studentId, schoolId);

        GetSchoolAlerts.Response item = Assert.Single((await Handle(dbContext, caller, schoolId)).Items);

        Assert.Null(item.ResolvedAt);
        Assert.Null(item.ResolvedBy);
        Assert.Null(item.ResolutionSource);
        Assert.Null(item.ResolutionReason);
    }

    /// <summary>
    ///     <b>Spec §3 and DEC-16, and the one test that distinguishes the chosen predicate from the
    ///     two rejected ones.</b>
    /// </summary>
    /// <remarks>
    ///     The alert was raised at school A; the student now attends school B. "Access follows
    ///     <c>Student.SchoolId</c>" is read literally, so B sees it and A does not.
    ///     <para>
    ///         It fails against <c>alert.SchoolId == schoolId</c> (school of record — A keeps seeing a
    ///         transferred child's alert), against the intersection of the two (nobody sees it, and
    ///         F07 cannot reach it either because its recount keys on the submitting school), and
    ///         against <c>WhereAuthorized(currentUser)</c>, which applies the predicate to
    ///         <c>alert.SchoolId</c> and is therefore the first reading in disguise.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task Handle_ScopesByStudentCurrentSchoolNotAlertSchool()
    {
        Guid formerSchoolId = Guid.NewGuid();
        Guid currentSchoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(formerSchoolId, currentSchoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, formerSchoolId, name: "Former");
        await SchoolSeed.AddAsync(dbContext, currentSchoolId, name: "Current");

        // The student has transferred: the alert still names the school that raised it.
        await StudentSeed.AddAsync(dbContext, currentSchoolId, studentId);
        StudentAlert alert = await AlertSeed.OpenAsync(dbContext, studentId, formerSchoolId);

        PagedResponse<GetSchoolAlerts.Response> receiving = await Handle(dbContext, caller, currentSchoolId);
        PagedResponse<GetSchoolAlerts.Response> former = await Handle(dbContext, caller, formerSchoolId);

        Assert.Equal(alert.Id, Assert.Single(receiving.Items).Id);
        Assert.Empty(former.Items);
    }

    [Fact]
    public async Task Handle_WhenStudentTransferred_MarksSchoolOfRecordAsPriorSchool()
    {
        Guid formerSchoolId = Guid.NewGuid();
        Guid currentSchoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(currentSchoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, formerSchoolId, name: "Former");
        await SchoolSeed.AddAsync(dbContext, currentSchoolId, name: "Current");
        await StudentSeed.AddAsync(dbContext, currentSchoolId, studentId);
        await AlertSeed.OpenAsync(dbContext, studentId, formerSchoolId);

        GetSchoolAlerts.Response item = Assert.Single((await Handle(dbContext, caller, currentSchoolId)).Items);

        Assert.Equal(GetSchoolAlerts.PriorSchoolOfRecord, item.SchoolOfRecord);
    }

    [Fact]
    public async Task Handle_WhenNotTransferred_MarksSchoolOfRecordAsCurrentSchool()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await AlertSeed.OpenAsync(dbContext, studentId, schoolId);

        GetSchoolAlerts.Response item = Assert.Single((await Handle(dbContext, caller, schoolId)).Items);

        Assert.Equal(GetSchoolAlerts.CurrentSchoolOfRecord, item.SchoolOfRecord);
    }

    /// <summary>
    ///     DEC-16's non-disclosure rule, asserted structurally so it cannot be regressed by adding a
    ///     field "for debugging". A prior school's id discloses where a child moved <em>from</em>,
    ///     which is the datum a care placement or domestic-abuse transfer must not leak.
    /// </summary>
    [Fact]
    public void Response_DeclaresNoSchoolIdMember()
    {
        string[] offending = typeof(GetSchoolAlerts.Response)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Where(name => name.Contains("SchoolId", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(
            offending.Length == 0,
            $"Response declares {string.Join(", ", offending)}. DEC-16 forbids returning a school id "
            + "on an alert; the schoolOfRecord discriminator carries everything a client needs.");
    }

    [Fact]
    public async Task Handle_ExcludesSoftDeletedAlerts()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        StudentAlert alert = await AlertSeed.OpenAsync(dbContext, studentId, schoolId);

        // Through the interceptor's delete rewrite, never by assigning IsDeleted (DEC-21).
        dbContext.StudentAlerts.Remove(alert);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        Assert.Empty((await Handle(dbContext, caller, schoolId)).Items);
    }

    [Fact]
    public async Task Handle_WhenSchoolOutsideScope_ThrowsNotFound()
    {
        Guid schoolId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(Guid.NewGuid());

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, caller, schoolId));

        Assert.Equal(ErrorCodes.Alert.NotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_ProjectsCurrentThresholdFromTheStudentsSchool()
    {
        Guid formerSchoolId = Guid.NewGuid();
        Guid currentSchoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(currentSchoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, formerSchoolId, name: "Former", threshold: 30);
        await SchoolSeed.AddAsync(dbContext, currentSchoolId, name: "Current", threshold: 12);
        await StudentSeed.AddAsync(dbContext, currentSchoolId, studentId);
        await AlertSeed.OpenAsync(dbContext, studentId, formerSchoolId);

        GetSchoolAlerts.Response item = Assert.Single((await Handle(dbContext, caller, currentSchoolId)).Items);

        Assert.Equal(12, item.CurrentThreshold);
    }

    [Fact]
    public async Task Handle_WhenSchoolThresholdIsNull_ProjectsTheDomainDefault()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId, threshold: null);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await AlertSeed.OpenAsync(dbContext, studentId, schoolId);

        GetSchoolAlerts.Response item = Assert.Single((await Handle(dbContext, caller, schoolId)).Items);

        Assert.Equal(AbsenceRules.DefaultThreshold, item.CurrentThreshold);
    }

    [Fact]
    public async Task Handle_WhenSchoolYearGiven_FiltersToThatYear()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        StudentAlert thisYear = await AlertSeed.OpenAsync(dbContext, studentId, schoolId, schoolYearStart: 2026);
        await AlertSeed.OpenAsync(dbContext, studentId, schoolId, schoolYearStart: 2025);

        PagedResponse<GetSchoolAlerts.Response> page = await Handle(
            dbContext, caller, schoolId, schoolYear: 2026);

        Assert.Equal(thisYear.Id, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task Handle_WhenSchoolYearAbsent_ReturnsEveryYear()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await AlertSeed.OpenAsync(dbContext, studentId, schoolId, schoolYearStart: 2026);
        await AlertSeed.OpenAsync(dbContext, studentId, schoolId, schoolYearStart: 2025);

        Assert.Equal(2, (await Handle(dbContext, caller, schoolId)).Page.TotalItems);
    }

    /// <summary>
    ///     Newest first — an alert list is a work queue. The clock is what separates the rows; tests
    ///     never hand-set <c>CreatedAt</c> (DEC-21).
    /// </summary>
    [Fact]
    public async Task Handle_OrdersByRaisedAtDescendingThenId()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock, caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        StudentAlert first = await AlertSeed.OpenAsync(dbContext, studentId, schoolId, schoolYearStart: 2024);
        clock.Advance(TimeSpan.FromHours(1));
        StudentAlert second = await AlertSeed.OpenAsync(dbContext, studentId, schoolId, schoolYearStart: 2025);
        clock.Advance(TimeSpan.FromHours(1));
        StudentAlert third = await AlertSeed.OpenAsync(dbContext, studentId, schoolId, schoolYearStart: 2026);

        PagedResponse<GetSchoolAlerts.Response> page = await Handle(dbContext, caller, schoolId);

        Assert.Equal([third.Id, second.Id, first.Id], page.Items.Select(item => item.Id).ToArray());
    }

    /// <summary>
    ///     The order must be <b>total</b>: a non-total order under the global <c>SplitQuery</c>
    ///     setting can repeat a row on one page and drop another (VC-27).
    /// </summary>
    [Fact]
    public async Task Handle_WhenRaisedAtTies_OrdersByIdAscending()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        // Same clock instant for both, so only the Id tiebreak can order them.
        Guid lower = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Guid higher = Guid.Parse("00000000-0000-0000-0000-000000000002");

        await AlertSeed.OpenAsync(dbContext, studentId, schoolId, higher, schoolYearStart: 2025);
        await AlertSeed.OpenAsync(dbContext, studentId, schoolId, lower, schoolYearStart: 2026);

        PagedResponse<GetSchoolAlerts.Response> page = await Handle(dbContext, caller, schoolId);

        Assert.Equal([lower, higher], page.Items.Select(item => item.Id).ToArray());
    }

    [Fact]
    public async Task Handle_WhenThresholdDrift_ReturnsOnlyAlertsWhoseRaiseThresholdDiffers()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        Guid otherStudentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId, threshold: 12);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await StudentSeed.AddAsync(dbContext, schoolId, otherStudentId);

        StudentAlert stranded = await AlertSeed.OpenAsync(
            dbContext, studentId, schoolId, thresholdAtRaise: 10);
        await AlertSeed.OpenAsync(dbContext, otherStudentId, schoolId, thresholdAtRaise: 12);

        PagedResponse<GetSchoolAlerts.Response> page = await Handle(
            dbContext, caller, schoolId, thresholdDrift: true);

        Assert.Equal(stranded.Id, Assert.Single(page.Items).Id);
    }

    /// <summary>
    ///     <b>The test that catches a second hard-coded <c>10</c> (plan R-5, L-10, V-26).</b> The
    ///     drift predicate cannot call <c>AbsenceRules.ResolveThreshold</c> — a pure static does not
    ///     translate — so it re-expresses it as <c>?? AbsenceRules.DefaultThreshold</c>. If the
    ///     coalesce is dropped, a null school threshold compares against <c>NULL</c> and neither row
    ///     comes back; if the constant is replaced by a literal, this still passes, which is why the
    ///     constant is named in the assertion rather than the number.
    /// </summary>
    [Fact]
    public async Task Handle_WhenSchoolThresholdIsNull_UsesTheDomainDefaultForDrift()
    {
        Guid schoolId = Guid.NewGuid();
        Guid atDefault = Guid.NewGuid();
        Guid aboveDefault = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId, threshold: null);
        await StudentSeed.AddAsync(dbContext, schoolId, atDefault);
        await StudentSeed.AddAsync(dbContext, schoolId, aboveDefault);

        await AlertSeed.OpenAsync(
            dbContext, atDefault, schoolId, thresholdAtRaise: AbsenceRules.DefaultThreshold);
        StudentAlert drifted = await AlertSeed.OpenAsync(
            dbContext, aboveDefault, schoolId, thresholdAtRaise: AbsenceRules.DefaultThreshold + 5);

        PagedResponse<GetSchoolAlerts.Response> page = await Handle(
            dbContext, caller, schoolId, thresholdDrift: true);

        Assert.Equal(drifted.Id, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task Handle_WhenThresholdDriftFalse_ReturnsEveryOpenAlert()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        Guid otherStudentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId, threshold: 12);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        await StudentSeed.AddAsync(dbContext, schoolId, otherStudentId);
        await AlertSeed.OpenAsync(dbContext, studentId, schoolId, thresholdAtRaise: 10);
        await AlertSeed.OpenAsync(dbContext, otherStudentId, schoolId, thresholdAtRaise: 12);

        Assert.Equal(
            2, (await Handle(dbContext, caller, schoolId, thresholdDrift: false)).Page.TotalItems);
    }

    /// <summary>
    ///     The flag is on every item, so a school sees the drift without having to ask for it — and
    ///     the flag and the filter are the same expression, so they cannot disagree.
    /// </summary>
    [Fact]
    public async Task Handle_AlwaysProjectsThresholdDriftEvenWhenNotFiltering()
    {
        Guid schoolId = Guid.NewGuid();
        Guid drifted = Guid.NewGuid();
        Guid aligned = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId, threshold: 12);
        await StudentSeed.AddAsync(dbContext, schoolId, drifted);
        await StudentSeed.AddAsync(dbContext, schoolId, aligned);
        StudentAlert driftedAlert = await AlertSeed.OpenAsync(
            dbContext, drifted, schoolId, thresholdAtRaise: 10);
        StudentAlert alignedAlert = await AlertSeed.OpenAsync(
            dbContext, aligned, schoolId, thresholdAtRaise: 12);

        PagedResponse<GetSchoolAlerts.Response> page = await Handle(dbContext, caller, schoolId);

        Assert.True(page.Items.Single(item => item.Id == driftedAlert.Id).ThresholdDrift);
        Assert.False(page.Items.Single(item => item.Id == alignedAlert.Id).ThresholdDrift);
    }

    /// <summary>
    ///     DEC-16 again, on the drift comparison: the governing threshold is the <b>receiving</b>
    ///     school's, not the school the alert names.
    /// </summary>
    [Fact]
    public async Task Handle_ThresholdDriftUsesTheStudentsCurrentSchool()
    {
        Guid formerSchoolId = Guid.NewGuid();
        Guid currentSchoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(currentSchoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);

        // The alert's own school still agrees with ThresholdAtRaise, so a comparison against
        // alert.SchoolId would report no drift. The receiving school's threshold differs.
        await SchoolSeed.AddAsync(dbContext, formerSchoolId, name: "Former", threshold: 10);
        await SchoolSeed.AddAsync(dbContext, currentSchoolId, name: "Current", threshold: 15);
        await StudentSeed.AddAsync(dbContext, currentSchoolId, studentId);
        StudentAlert alert = await AlertSeed.OpenAsync(
            dbContext, studentId, formerSchoolId, thresholdAtRaise: 10);

        PagedResponse<GetSchoolAlerts.Response> page = await Handle(
            dbContext, caller, currentSchoolId, thresholdDrift: true);

        Assert.Equal(alert.Id, Assert.Single(page.Items).Id);
        Assert.Equal(15, page.Items[0].CurrentThreshold);
    }

    /// <summary>
    ///     <c>ResolveAlert</c> builds its 200 body from entities it already holds rather than issuing
    ///     the projected query a second time, so the two constructions of <see cref="GetSchoolAlerts.Row" />
    ///     could drift. <see cref="GetSchoolAlerts.Row" /> is a record, so one equality assertion
    ///     covers every member — including any member added later.
    /// </summary>
    /// <remarks>
    ///     <b>The null threshold is a separate case and the theory exists for it.</b> As a single
    ///     <c>[Fact]</c> seeding a configured threshold, this test was inert against the mutation it
    ///     is meant to catch: changing one of the two <c>?? AbsenceRules.DefaultThreshold</c>
    ///     expressions and not the other left it green, because the coalesce never fires when the
    ///     school has a threshold. Found by mutation, not by review.
    ///     <para>
    ///         Both lifecycle states run too: the four resolution members are null on an open episode,
    ///         so an open-only fixture would not compare them at all.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(12, true)]
    [InlineData(null, true)]
    [InlineData(12, false)]
    [InlineData(null, false)]
    public async Task ToRow_MatchesTheProjectedRow(int? threshold, bool resolved)
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        School school = await SchoolSeed.AddAsync(dbContext, schoolId, threshold: threshold);
        Student student = await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        StudentAlert alert = resolved
            ? await AlertSeed.ResolvedAsync(dbContext, studentId, schoolId)
            : await AlertSeed.OpenAsync(dbContext, studentId, schoolId);

        GetSchoolAlerts.StatusFilter status = resolved
            ? GetSchoolAlerts.StatusFilter.Resolved
            : GetSchoolAlerts.StatusFilter.Open;

        GetSchoolAlerts.Row projected = await GetSchoolAlerts
            .AlertRows(dbContext, schoolId, status, schoolYear: null, thresholdDrift: false)
            .SingleAsync(CancellationToken.None);

        Assert.Equal(projected, GetSchoolAlerts.ToRow(alert, student, school));
    }

    private static Task<PagedResponse<GetSchoolAlerts.Response>> Handle(
        SparkrockRwcDbContext dbContext,
        ICurrentUser caller,
        Guid schoolId,
        string? status = null,
        int? schoolYear = null,
        bool? thresholdDrift = null,
        int? page = null,
        int? pageSize = null)
    {
        GetSchoolAlerts.QueryHandler handler = new(dbContext, caller);

        return handler.Handle(
            new GetSchoolAlerts.Query
            {
                SchoolId = schoolId,
                Status = status,
                SchoolYear = schoolYear,
                ThresholdDrift = thresholdDrift,
                Page = page,
                PageSize = pageSize
            },
            CancellationToken.None);
    }
}
