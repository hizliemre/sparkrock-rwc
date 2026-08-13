using System.Reflection;
using domain.Exceptions;
using domain.SchoolTerms;
using features.SchoolTerms;
using features.tests.Fakes;
using FluentValidation.Results;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace features.tests.SchoolTerms;

public sealed class UpdateSchoolTermValidatorTests
{
    [Fact]
    public void Validate_WhenNameIsEmpty_Fails() =>
        Assert.Equal(
            ErrorCodes.Validation.RequiredField,
            Single(Validate(Command(name: string.Empty)), nameof(UpdateSchoolTerm.Command.Name)).ErrorCode);

    [Fact]
    public void Validate_WhenNameExceeds100_Fails() =>
        Assert.Equal(
            ErrorCodes.Validation.Failed,
            Single(
                    Validate(Command(name: new string('x', CreateSchoolTerm.MaxNameLength + 1))),
                    nameof(UpdateSchoolTerm.Command.Name))
                .ErrorCode);

    [Fact]
    public void Validate_WhenEndDateIsBeforeStartDate_Fails() =>
        Assert.Equal(
            ErrorCodes.Validation.Failed,
            Single(
                    Validate(Command(
                        startDate: new DateOnly(2026, 12, 20), endDate: new DateOnly(2026, 12, 19))),
                    nameof(UpdateSchoolTerm.Command.EndDate))
                .ErrorCode);

    [Fact]
    public void Validate_WhenEndDateEqualsStartDate_Succeeds() =>
        Assert.True(
            Validate(Command(startDate: new DateOnly(2026, 9, 1), endDate: new DateOnly(2026, 9, 1))).IsValid);

    [Fact]
    public void Validate_WhenStartDateIsAbsent_Fails()
    {
        UpdateSchoolTerm.Command command = new()
        {
            SchoolId = Guid.NewGuid(),
            TermId = Guid.NewGuid(),
            Name = SchoolTermSeed.DefaultName,
            StartDate = null,
            EndDate = SchoolTermSeed.DefaultEnd,
            IsActive = true
        };

        Assert.Equal(
            ErrorCodes.Validation.RequiredField,
            Single(Validate(command), nameof(UpdateSchoolTerm.Command.StartDate)).ErrorCode);
    }

    /// <summary>
    ///     <c>PUT</c> is a replace, so an optional flag makes absent and false indistinguishable —
    ///     every partial update sent by a client that did not know about the flag would silently
    ///     deactivate the term.
    /// </summary>
    [Fact]
    public void Validate_WhenIsActiveIsAbsent_Fails() =>
        Assert.Equal(
            ErrorCodes.Validation.RequiredField,
            Single(Validate(Command(isActive: null)), nameof(UpdateSchoolTerm.Command.IsActive)).ErrorCode);

    [Fact]
    public void Validate_WhenEverythingIsPresentAndOrdered_Succeeds() => Assert.True(Validate(Command()).IsValid);

    private static ValidationFailure Single(ValidationResult result, string propertyName) =>
        Assert.Single(result.Errors, failure => failure.PropertyName == propertyName);

    private static ValidationResult Validate(UpdateSchoolTerm.Command command) =>
        new UpdateSchoolTerm.CommandValidator().Validate(command);

    private static UpdateSchoolTerm.Command Command(
        string name = SchoolTermSeed.DefaultName,
        DateOnly? startDate = null,
        DateOnly? endDate = null,
        bool? isActive = true) => new()
    {
        SchoolId = Guid.NewGuid(),
        TermId = Guid.NewGuid(),
        Name = name,
        StartDate = startDate ?? SchoolTermSeed.DefaultStart,
        EndDate = endDate ?? SchoolTermSeed.DefaultEnd,
        IsActive = isActive
    };
}

public sealed class UpdateSchoolTermHandlerTests
{
    [Fact]
    public async Task Handle_UpdatesNameAndDates()
    {
        Guid schoolId = Guid.NewGuid();
        Guid termId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolTermSeed.AddAsync(dbContext, schoolId, termId);

        GetSchoolTermById.Response response = await Handle(
            dbContext, caller, schoolId, termId, name: "Autumn",
            startDate: new DateOnly(2026, 9, 8), endDate: new DateOnly(2026, 12, 18));

        Assert.Equal("Autumn", response.Name);
        Assert.Equal(new DateOnly(2026, 9, 8), response.StartDate);
        Assert.Equal(new DateOnly(2026, 12, 18), response.EndDate);

        SchoolTerm persisted = Assert.Single(await dbContext.SchoolTerms.AsNoTracking().ToListAsync());
        Assert.Equal("Autumn", persisted.Name);
        Assert.Equal(new DateOnly(2026, 9, 8), persisted.StartDate);
        Assert.Equal(new DateOnly(2026, 12, 18), persisted.EndDate);
    }

    /// <summary>The self-exclusion, through the handler: a term does not conflict with itself.</summary>
    [Fact]
    public async Task Handle_WhenDatesUnchanged_DoesNotConflictWithItself()
    {
        Guid schoolId = Guid.NewGuid();
        Guid termId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolTermSeed.AddAsync(dbContext, schoolId, termId);

        GetSchoolTermById.Response response = await Handle(
            dbContext, caller, schoolId, termId, name: "Renamed only");

        Assert.Equal("Renamed only", response.Name);
        Assert.Equal(SchoolTermSeed.DefaultStart, response.StartDate);
        Assert.Equal(SchoolTermSeed.DefaultEnd, response.EndDate);
    }

    [Fact]
    public async Task Handle_WhenNewDatesOverlapAnotherActiveTerm_ThrowsConflict()
    {
        Guid schoolId = Guid.NewGuid();
        Guid termId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolTermSeed.AddAsync(dbContext, schoolId, name: "Term 1");
        await SchoolTermSeed.AddAsync(
            dbContext, schoolId, termId, "Term 2",
            new DateOnly(2027, 1, 5), new DateOnly(2027, 3, 31));

        ConflictException exception = await Assert.ThrowsAsync<ConflictException>(
            () => Handle(
                dbContext, caller, schoolId, termId, name: "Term 2",
                startDate: new DateOnly(2026, 12, 1), endDate: new DateOnly(2027, 3, 31)));

        Assert.Equal(ErrorCodes.Term.Overlap, exception.ErrorCode);

        SchoolTerm unchanged = await dbContext.SchoolTerms.AsNoTracking().SingleAsync(term => term.Id == termId);
        Assert.Equal(new DateOnly(2027, 1, 5), unchanged.StartDate);
    }

    /// <summary>
    ///     Moving a term to start on another active term's <em>last</em> day is a conflict, because
    ///     both bounds are inclusive; the day after is not. The two calls together are what separate
    ///     closed bounds from half-open at this tier.
    /// </summary>
    [Fact]
    public async Task Handle_WhenNewDatesStartOnAnotherActiveTermsLastDay_ThrowsConflict()
    {
        Guid schoolId = Guid.NewGuid();
        Guid termId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolTermSeed.AddAsync(dbContext, schoolId, name: "Term 1");
        await SchoolTermSeed.AddAsync(
            dbContext, schoolId, termId, "Term 2",
            new DateOnly(2027, 1, 5), new DateOnly(2027, 3, 31));

        ConflictException exception = await Assert.ThrowsAsync<ConflictException>(
            () => Handle(
                dbContext, caller, schoolId, termId, name: "Term 2",
                startDate: new DateOnly(2026, 12, 20), endDate: new DateOnly(2027, 3, 31)));

        Assert.Equal(ErrorCodes.Term.Overlap, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WhenNewDatesAbutAnotherActiveTerm_Succeeds()
    {
        Guid schoolId = Guid.NewGuid();
        Guid termId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolTermSeed.AddAsync(dbContext, schoolId, name: "Term 1");
        await SchoolTermSeed.AddAsync(
            dbContext, schoolId, termId, "Term 2",
            new DateOnly(2027, 1, 5), new DateOnly(2027, 3, 31));

        GetSchoolTermById.Response response = await Handle(
            dbContext, caller, schoolId, termId, name: "Term 2",
            startDate: new DateOnly(2026, 12, 21), endDate: new DateOnly(2027, 3, 31));

        Assert.Equal(new DateOnly(2026, 12, 21), response.StartDate);
    }

    /// <summary>
    ///     <b>V-19's <c>Verified by</c>, half two, and the case most likely to ship unguarded.</b> A
    ///     <c>PUT { "isActive": true }</c> on a superseded term reads as a flag flip, and it is the one
    ///     flag flip that can violate the invariant — the overlap probe is easy to attach only to the
    ///     date-change branch.
    /// </summary>
    [Fact]
    public async Task Handle_WhenReactivatingIntoAnOverlap_ThrowsConflict()
    {
        Guid schoolId = Guid.NewGuid();
        Guid supersededId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolTermSeed.AddAsync(dbContext, schoolId, name: "Term 1");
        await SchoolTermSeed.AddAsync(
            dbContext, schoolId, supersededId, "Fall (superseded)", isActive: false);

        ConflictException exception = await Assert.ThrowsAsync<ConflictException>(
            () => Handle(dbContext, caller, schoolId, supersededId, name: "Fall (superseded)"));

        Assert.Equal(ErrorCodes.Term.Overlap, exception.ErrorCode);
        Assert.Contains("Term 1", exception.Message, StringComparison.Ordinal);

        SchoolTerm stillInactive = await dbContext.SchoolTerms.AsNoTracking()
            .SingleAsync(term => term.Id == supersededId);
        Assert.False(stillInactive.IsActive);
    }

    /// <summary>Reactivating a term whose dates are free succeeds — the probe is not a blanket refusal.</summary>
    [Fact]
    public async Task Handle_WhenReactivatingIntoFreeDates_Succeeds()
    {
        Guid schoolId = Guid.NewGuid();
        Guid supersededId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolTermSeed.AddAsync(dbContext, schoolId, supersededId, "Parked", isActive: false);

        GetSchoolTermById.Response response = await Handle(
            dbContext, caller, schoolId, supersededId, name: "Parked");

        Assert.True(response.IsActive);
    }

    /// <summary>
    ///     An inactive term may overlap anything, so a term being parked must not be rejected for
    ///     overlapping the thing that replaced it. The probe runs against the <em>resulting</em>
    ///     state, not the incoming flag.
    /// </summary>
    [Fact]
    public async Task Handle_WhenDeactivating_SkipsTheOverlapProbe()
    {
        Guid schoolId = Guid.NewGuid();
        Guid termId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolTermSeed.AddAsync(dbContext, schoolId, termId, "Being parked");

        // A second active term covering exactly the same dates cannot be created through the API, but
        // it can arrive through F12's import or through the accepted concurrent-POST race. Parking one
        // of the pair is the documented recovery, so it must not be refused.
        dbContext.SchoolTerms.Add(new SchoolTerm
        {
            Id = Guid.NewGuid(),
            SchoolId = schoolId,
            Name = "Replacement",
            StartDate = SchoolTermSeed.DefaultStart,
            EndDate = SchoolTermSeed.DefaultEnd
        });
        await dbContext.SaveChangesAsync(CancellationToken.None);

        GetSchoolTermById.Response response = await Handle(
            dbContext, caller, schoolId, termId, name: "Being parked", isActive: false);

        Assert.False(response.IsActive);
    }

    [Fact]
    public async Task Handle_WhenTermDoesNotExist_ThrowsNotFound()
    {
        Guid schoolId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, caller, schoolId, Guid.NewGuid()));

        Assert.Equal(ErrorCodes.Term.NotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WhenTermBelongsToAnotherSchool_ThrowsNotFound()
    {
        Guid addressedSchoolId = Guid.NewGuid();
        Guid owningSchoolId = Guid.NewGuid();
        Guid termId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(addressedSchoolId, owningSchoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolTermSeed.AddAsync(dbContext, owningSchoolId, termId, "Theirs");

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, caller, addressedSchoolId, termId, name: "Hijacked"));

        Assert.Equal(ErrorCodes.Term.NotFound, exception.ErrorCode);
        Assert.Equal("Theirs", Assert.Single(await dbContext.SchoolTerms.AsNoTracking().ToListAsync()).Name);
    }

    [Fact]
    public async Task Handle_WhenSchoolIsOutsideScope_ThrowsNotFound()
    {
        Guid schoolId = Guid.NewGuid();
        Guid termId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(Guid.NewGuid());

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolTermSeed.AddAsync(dbContext, schoolId, termId);

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, caller, schoolId, termId));

        Assert.Equal(ErrorCodes.School.NotFound, exception.ErrorCode);
    }

    /// <summary>
    ///     No 403 anywhere in this feature: a non-admin scoped to the school may both deactivate and
    ///     reactivate through <c>PUT</c>.
    /// </summary>
    [Fact]
    public async Task Handle_NeverThrowsForbidden()
    {
        Guid schoolId = Guid.NewGuid();
        Guid termId = Guid.NewGuid();
        FakeCurrentUser nonAdmin = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: nonAdmin);
        await SchoolTermSeed.AddAsync(dbContext, schoolId, termId);

        Assert.False(nonAdmin.IsSystemAdmin);

        GetSchoolTermById.Response deactivated = await Handle(
            dbContext, nonAdmin, schoolId, termId, isActive: false);
        Assert.False(deactivated.IsActive);

        GetSchoolTermById.Response reactivated = await Handle(dbContext, nonAdmin, schoolId, termId);
        Assert.True(reactivated.IsActive);
    }

    /// <summary>Audit fields are never hand-set (DEC-21); the clock is advanced instead.</summary>
    [Fact]
    public async Task Handle_StampsModifiedAt()
    {
        Guid schoolId = Guid.NewGuid();
        Guid termId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock, caller);
        await SchoolTermSeed.AddAsync(dbContext, schoolId, termId);

        clock.Advance(TimeSpan.FromHours(4));
        DateTimeOffset modifiedAt = clock.GetUtcNow();

        GetSchoolTermById.Response response = await Handle(
            dbContext, caller, schoolId, termId, name: "Renamed");

        Assert.Equal(modifiedAt, response.LastUpdatedAt);
        Assert.Equal(InMemoryDbContextFactory.DefaultNow, response.CreatedAt);
    }

    /// <summary>Route values are authoritative; the body repeats neither id.</summary>
    [Fact]
    public void Handle_DoesNotAcceptRouteIdsInTheBody()
    {
        string[] members = typeof(UpdateSchoolTerm.Body)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(members, name => name.Equals("SchoolId", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(members, name => name.Equals("TermId", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(members, name => name.Equals("Id", StringComparison.OrdinalIgnoreCase));
    }

    private static Task<GetSchoolTermById.Response> Handle(
        SparkrockRwcDbContext dbContext,
        FakeCurrentUser currentUser,
        Guid schoolId,
        Guid termId,
        string name = SchoolTermSeed.DefaultName,
        DateOnly? startDate = null,
        DateOnly? endDate = null,
        bool isActive = true)
    {
        UpdateSchoolTerm.CommandHandler handler = new(
            dbContext, currentUser, NullLogger<UpdateSchoolTerm.CommandHandler>.Instance);

        return handler.Handle(
            new UpdateSchoolTerm.Command
            {
                SchoolId = schoolId,
                TermId = termId,
                Name = name,
                StartDate = startDate ?? SchoolTermSeed.DefaultStart,
                EndDate = endDate ?? SchoolTermSeed.DefaultEnd,
                IsActive = isActive
            },
            CancellationToken.None);
    }
}
