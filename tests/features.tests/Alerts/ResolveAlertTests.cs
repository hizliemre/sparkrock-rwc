using System.Reflection;
using domain.Alerts;
using domain.Exceptions;
using features.Alerts;
using features.tests.Fakes;
using features.tests.Schools;
using features.tests.Students;
using FluentValidation.Results;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace features.tests.Alerts;

public sealed class ResolveAlertValidatorTests
{
    private static readonly ResolveAlert.CommandValidator Validator = new();

    /// <summary>
    ///     A manual resolution permanently suppresses re-raising for that student, type, year and
    ///     school for the rest of the school year (DEC-18). An unexplained permanent suppression of a
    ///     safeguarding signal is not a state this API will create.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\n")]
    public void Validate_WhenReasonIsBlank_Fails(string? reason)
    {
        ValidationResult result = Validator.Validate(Command(reason));

        Assert.False(result.IsValid);
        Assert.Equal(ErrorCodes.Validation.RequiredField, Assert.Single(result.Errors).ErrorCode);
    }

    /// <summary>The bound mirrors <c>resolution_reason varchar(500)</c> exactly (DEC-06).</summary>
    [Fact]
    public void Validate_WhenReasonExceedsMaxLength_Fails()
    {
        ValidationResult result = Validator.Validate(Command(new string('x', ResolveAlert.MaxReasonLength + 1)));

        Assert.False(result.IsValid);
        Assert.Equal(ErrorCodes.Validation.Failed, Assert.Single(result.Errors).ErrorCode);
    }

    [Fact]
    public void Validate_WhenReasonIsAtMaxLength_Succeeds()
    {
        Assert.True(Validator.Validate(Command(new string('x', ResolveAlert.MaxReasonLength))).IsValid);
    }

    [Fact]
    public void Validate_WhenReasonIsPresent_Succeeds()
    {
        Assert.True(Validator.Validate(Command("Home visit completed; attendance plan agreed.")).IsValid);
    }

    private static ResolveAlert.Command Command(string? reason) =>
        new() { AlertId = Guid.NewGuid(), Reason = reason! };
}

public sealed class ResolveAlertHandlerTests
{
    private const string Reason = "Home visit completed 2026-11-05; attendance plan agreed with family.";

    [Fact]
    public async Task Handle_SetsResolvedAtResolvedByAndSourceManual()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock, caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        StudentAlert alert = await AlertSeed.OpenAsync(dbContext, studentId, schoolId);

        clock.Advance(TimeSpan.FromHours(3));

        await Handle(dbContext, caller, clock, alert.Id);

        StudentAlert persisted = Assert.Single(await dbContext.StudentAlerts.AsNoTracking().ToListAsync());

        Assert.Equal(clock.GetUtcNow(), persisted.ResolvedAt);
        Assert.Equal(caller.UserId, persisted.ResolvedBy);
        Assert.Equal(ResolutionSource.Manual, persisted.ResolutionSource);
        Assert.Equal(Reason, persisted.ResolutionReason);
    }

    [Fact]
    public async Task Handle_ReturnsTheUpdatedAlert()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock, caller);
        await SchoolSeed.AddAsync(dbContext, schoolId, threshold: 12);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId, firstName: "Ada", lastName: "Byron");
        StudentAlert alert = await AlertSeed.OpenAsync(dbContext, studentId, schoolId, thresholdAtRaise: 10);

        clock.Advance(TimeSpan.FromHours(3));

        GetSchoolAlerts.Response response = await Handle(dbContext, caller, clock, alert.Id);

        Assert.Equal(alert.Id, response.Id);
        Assert.Equal(GetSchoolAlerts.ResolvedStatus, response.Status);
        Assert.Equal(clock.GetUtcNow(), response.ResolvedAt);
        Assert.Equal(caller.UserId, response.ResolvedBy);
        Assert.Equal(nameof(ResolutionSource.Manual), response.ResolutionSource);
        Assert.Equal(Reason, response.ResolutionReason);

        // The list projection, reached through the same Response record.
        Assert.Equal("Ada", response.StudentFirstName);
        Assert.Equal(12, response.CurrentThreshold);
        Assert.True(response.ThresholdDrift);
        Assert.Equal(GetSchoolAlerts.CurrentSchoolOfRecord, response.SchoolOfRecord);
    }

    /// <summary>
    ///     <c>ResolutionSource</c> is never accepted from the body. <c>AutoBelowThreshold</c> is F07's
    ///     alone, and the ability to forge it would let a client disguise a human decision as an
    ///     automatic one — which is exactly the distinction <c>AlertRules.ShouldRaise</c>'s
    ///     <c>hasManualResolutionThisYear</c> argument depends on.
    /// </summary>
    [Fact]
    public void Command_DeclaresNoResolutionSourceMember()
    {
        string[] members = typeof(ResolveAlert.Command)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain("ResolutionSource", members, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Source", members, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Handle_WhenAlreadyResolved_ThrowsConflict()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        StudentAlert alert = await AlertSeed.ResolvedAsync(dbContext, studentId, schoolId);

        ConflictException exception = await Assert.ThrowsAsync<ConflictException>(
            () => Handle(dbContext, caller, InMemoryDbContextFactory.Clock(), alert.Id));

        Assert.Equal(ErrorCodes.Alert.AlreadyResolved, exception.ErrorCode);
    }

    /// <summary>
    ///     The 409 guard, proved to be doing the work.
    /// </summary>
    /// <remarks>
    ///     A <c>…_DoesNotOverwrite</c> assertion would not prove it: with the same reason and the same
    ///     resolver, EF's change tracker reports no modification either way. The absent log line is
    ///     the discriminator — a handler that skipped the guard announces a resolution that the first
    ///     resolver already made.
    /// </remarks>
    [Fact]
    public async Task Handle_WhenAlreadyResolved_DoesNotLogAResolution()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);
        RecordingLogger<ResolveAlert.CommandHandler> logger = new();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        StudentAlert alert = await AlertSeed.ResolvedAsync(dbContext, studentId, schoolId);

        ResolveAlert.CommandHandler handler = new(
            dbContext, caller, InMemoryDbContextFactory.Clock(), logger);

        await Assert.ThrowsAsync<ConflictException>(
            () => handler.Handle(
                new ResolveAlert.Command { AlertId = alert.Id, Reason = Reason },
                CancellationToken.None));

        Assert.Empty(logger.EventIds);
    }

    /// <summary>
    ///     The positive half, so the assertion above cannot pass because the slice never logs at all.
    /// </summary>
    [Fact]
    public async Task Handle_LogsTheResolutionOnce()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);
        RecordingLogger<ResolveAlert.CommandHandler> logger = new();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        StudentAlert alert = await AlertSeed.OpenAsync(dbContext, studentId, schoolId);

        ResolveAlert.CommandHandler handler = new(
            dbContext, caller, InMemoryDbContextFactory.Clock(), logger);

        await handler.Handle(
            new ResolveAlert.Command { AlertId = alert.Id, Reason = Reason },
            CancellationToken.None);

        Assert.Equal([ResolveAlert.AlertResolvedEventId], logger.EventIds);
    }

    [Fact]
    public async Task Handle_WhenAlertIdUnknown_ThrowsNotFound()
    {
        Guid schoolId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, caller, InMemoryDbContextFactory.Clock(), Guid.NewGuid()));

        Assert.Equal(ErrorCodes.Alert.NotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WhenAlertSoftDeleted_ThrowsNotFound()
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

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, caller, InMemoryDbContextFactory.Clock(), alert.Id));

        Assert.Equal(ErrorCodes.Alert.NotFound, exception.ErrorCode);
    }

    /// <summary>
    ///     Scope follows the student's <b>current</b> school (DEC-16), so the school that raised the
    ///     alert cannot resolve it once the student has left. V-28's accepted cost, at the point it
    ///     becomes visible.
    /// </summary>
    [Fact]
    public async Task Handle_WhenCallerHoldsOnlyTheAlertsSchool_ThrowsNotFound()
    {
        Guid formerSchoolId = Guid.NewGuid();
        Guid currentSchoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(formerSchoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, formerSchoolId, name: "Former");
        await SchoolSeed.AddAsync(dbContext, currentSchoolId, name: "Current");
        await StudentSeed.AddAsync(dbContext, currentSchoolId, studentId);
        StudentAlert alert = await AlertSeed.OpenAsync(dbContext, studentId, formerSchoolId);

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, caller, InMemoryDbContextFactory.Clock(), alert.Id));

        Assert.Equal(ErrorCodes.Alert.NotFound, exception.ErrorCode);
        Assert.Null(Assert.Single(await dbContext.StudentAlerts.AsNoTracking().ToListAsync()).ResolvedAt);
    }

    /// <summary>
    ///     The other side of the same rule: the <b>receiving</b> school inherits the prior school's
    ///     open episode and can close it. Without this, a transferred child's episode is stranded
    ///     where nothing — including F07's auto-resolve, which keys on the submitting school — can
    ///     ever reach it.
    /// </summary>
    [Fact]
    public async Task Handle_WhenAlertWasRaisedAtAPriorSchool_ResolvesForTheReceivingSchool()
    {
        Guid formerSchoolId = Guid.NewGuid();
        Guid currentSchoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(currentSchoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, formerSchoolId, name: "Former");
        await SchoolSeed.AddAsync(dbContext, currentSchoolId, name: "Current");
        await StudentSeed.AddAsync(dbContext, currentSchoolId, studentId);
        StudentAlert alert = await AlertSeed.OpenAsync(dbContext, studentId, formerSchoolId);

        GetSchoolAlerts.Response response = await Handle(
            dbContext, caller, InMemoryDbContextFactory.Clock(), alert.Id);

        Assert.Equal(GetSchoolAlerts.ResolvedStatus, response.Status);
        Assert.Equal(GetSchoolAlerts.PriorSchoolOfRecord, response.SchoolOfRecord);
        Assert.NotNull(Assert.Single(await dbContext.StudentAlerts.AsNoTracking().ToListAsync()).ResolvedAt);
    }

    /// <summary>
    ///     Conventions §2's existence-oracle rule. <see cref="NotFoundException" /> takes no message
    ///     parameter, so identical payloads are true by construction; this test guards the
    ///     construction — an <c>ALERT.OUT_OF_SCOPE</c> added later would fail here.
    /// </summary>
    [Fact]
    public async Task Handle_UnknownIdAndOutOfScopeIdProduceIdenticalExceptions()
    {
        Guid formerSchoolId = Guid.NewGuid();
        Guid currentSchoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(formerSchoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, formerSchoolId, name: "Former");
        await SchoolSeed.AddAsync(dbContext, currentSchoolId, name: "Current");
        await StudentSeed.AddAsync(dbContext, currentSchoolId, studentId);
        StudentAlert outOfScope = await AlertSeed.OpenAsync(dbContext, studentId, formerSchoolId);

        NotFoundException unknown = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, caller, InMemoryDbContextFactory.Clock(), Guid.NewGuid()));
        NotFoundException forbidden = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, caller, InMemoryDbContextFactory.Clock(), outOfScope.Id));

        Assert.Equal(unknown.ErrorCode, forbidden.ErrorCode);
        Assert.Equal(unknown.Message, forbidden.Message);
    }

    [Fact]
    public async Task Handle_DoesNotLeaveUnsavedChanges()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        StudentAlert alert = await AlertSeed.OpenAsync(dbContext, studentId, schoolId);

        await Handle(dbContext, caller, InMemoryDbContextFactory.Clock(), alert.Id);

        Assert.False(dbContext.ChangeTracker.HasChanges());
    }

    /// <summary>
    ///     <c>ModifiedAt</c> is the interceptor's (DEC-21) and proves the write reached the database
    ///     rather than only the tracked instance the handler held.
    /// </summary>
    [Fact]
    public async Task Handle_StampsModifiedAtThroughTheInterceptor()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock, caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        StudentAlert alert = await AlertSeed.OpenAsync(dbContext, studentId, schoolId);
        Assert.Null(alert.ModifiedAt);

        clock.Advance(TimeSpan.FromHours(3));

        await Handle(dbContext, caller, clock, alert.Id);

        Assert.Equal(
            clock.GetUtcNow(),
            Assert.Single(await dbContext.StudentAlerts.AsNoTracking().ToListAsync()).ModifiedAt);
    }

    private static Task<GetSchoolAlerts.Response> Handle(
        SparkrockRwcDbContext dbContext,
        FakeCurrentUser caller,
        TimeProvider clock,
        Guid alertId,
        string reason = Reason)
    {
        ResolveAlert.CommandHandler handler = new(
            dbContext, caller, clock, NullLogger<ResolveAlert.CommandHandler>.Instance);

        return handler.Handle(
            new ResolveAlert.Command { AlertId = alertId, Reason = reason },
            CancellationToken.None);
    }
}
