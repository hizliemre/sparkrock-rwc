using System.Reflection;
using domain.Exceptions;
using domain.SchoolTerms;
using features.SchoolTerms;
using features.tests.Fakes;
using features.tests.Schools;
using FluentValidation.Results;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace features.tests.SchoolTerms;

public sealed class CreateSchoolTermValidatorTests
{
    [Fact]
    public void Validate_WhenNameIsEmpty_Fails() =>
        Assert.Equal(
            ErrorCodes.Validation.RequiredField,
            Single(Validate(Command(name: string.Empty)), nameof(CreateSchoolTerm.Command.Name)).ErrorCode);

    /// <summary>Mirrors F01c §3's <c>school_terms.name varchar(100)</c>.</summary>
    [Fact]
    public void Validate_WhenNameExceeds100_Fails() =>
        Assert.Equal(
            ErrorCodes.Validation.Failed,
            Single(
                    Validate(Command(name: new string('x', CreateSchoolTerm.MaxNameLength + 1))),
                    nameof(CreateSchoolTerm.Command.Name))
                .ErrorCode);

    [Fact]
    public void Validate_WhenNameIsExactly100_Succeeds() =>
        Assert.True(Validate(Command(name: new string('x', CreateSchoolTerm.MaxNameLength))).IsValid);

    /// <summary>
    ///     The dates are nullable on the command precisely so that "absent" is distinguishable from a
    ///     default <see cref="DateOnly" />. A non-nullable member would bind an omitted field to
    ///     0001-01-01 and store it.
    /// </summary>
    [Fact]
    public void Validate_WhenStartDateIsAbsent_Fails()
    {
        CreateSchoolTerm.Command command = new()
        {
            SchoolId = Guid.NewGuid(),
            Name = SchoolTermSeed.DefaultName,
            StartDate = null,
            EndDate = SchoolTermSeed.DefaultEnd
        };

        Assert.Equal(
            ErrorCodes.Validation.RequiredField,
            Single(Validate(command), nameof(CreateSchoolTerm.Command.StartDate)).ErrorCode);
    }

    [Fact]
    public void Validate_WhenEndDateIsAbsent_Fails()
    {
        CreateSchoolTerm.Command command = new()
        {
            SchoolId = Guid.NewGuid(),
            Name = SchoolTermSeed.DefaultName,
            StartDate = SchoolTermSeed.DefaultStart,
            EndDate = null
        };

        Assert.Equal(
            ErrorCodes.Validation.RequiredField,
            Single(Validate(command), nameof(CreateSchoolTerm.Command.EndDate)).ErrorCode);
    }

    /// <summary>Mirrors <c>ck_school_terms_end_date_not_before_start_date</c>.</summary>
    [Fact]
    public void Validate_WhenEndDateIsBeforeStartDate_Fails() =>
        Assert.Equal(
            ErrorCodes.Validation.Failed,
            Single(
                    Validate(Command(
                        startDate: new DateOnly(2026, 12, 20), endDate: new DateOnly(2026, 12, 19))),
                    nameof(CreateSchoolTerm.Command.EndDate))
                .ErrorCode);

    /// <summary>A one-day term is legal under closed bounds.</summary>
    [Fact]
    public void Validate_WhenEndDateEqualsStartDate_Succeeds() =>
        Assert.True(
            Validate(Command(startDate: new DateOnly(2026, 9, 1), endDate: new DateOnly(2026, 9, 1))).IsValid);

    [Fact]
    public void Validate_WhenEverythingIsPresentAndOrdered_Succeeds() => Assert.True(Validate(Command()).IsValid);

    private static ValidationFailure Single(ValidationResult result, string propertyName) =>
        Assert.Single(result.Errors, failure => failure.PropertyName == propertyName);

    private static ValidationResult Validate(CreateSchoolTerm.Command command) =>
        new CreateSchoolTerm.CommandValidator().Validate(command);

    private static CreateSchoolTerm.Command Command(
        string name = SchoolTermSeed.DefaultName,
        DateOnly? startDate = null,
        DateOnly? endDate = null) => new()
    {
        SchoolId = Guid.NewGuid(),
        Name = name,
        StartDate = startDate ?? SchoolTermSeed.DefaultStart,
        EndDate = endDate ?? SchoolTermSeed.DefaultEnd
    };
}

public sealed class CreateSchoolTermHandlerTests
{
    /// <summary>
    ///     Created active, always. A term created inactive would skip the overlap probe and sit
    ///     waiting to violate the invariant on its first reactivation.
    /// </summary>
    [Fact]
    public async Task Handle_PersistsTheTermAsActive()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);

        await Handle(dbContext, FakeCurrentUser.ScopedTo(schoolId), schoolId);

        SchoolTerm persisted = Assert.Single(await dbContext.SchoolTerms.AsNoTracking().ToListAsync());
        Assert.True(persisted.IsActive);
        Assert.Equal(schoolId, persisted.SchoolId);
        Assert.Equal(SchoolTermSeed.DefaultStart, persisted.StartDate);
        Assert.Equal(SchoolTermSeed.DefaultEnd, persisted.EndDate);
    }

    [Fact]
    public async Task Handle_ReturnsTheCreatedResponse()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);

        GetSchoolTermById.Response response = await Handle(
            dbContext, FakeCurrentUser.ScopedTo(schoolId), schoolId, name: "Term 1");

        Assert.NotEqual(Guid.Empty, response.Id);
        Assert.Equal(schoolId, response.SchoolId);
        Assert.Equal("Term 1", response.Name);
        Assert.True(response.IsActive);
        Assert.Equal(InMemoryDbContextFactory.DefaultNow, response.CreatedAt);
        Assert.Equal(InMemoryDbContextFactory.DefaultNow, response.LastUpdatedAt);
    }

    /// <summary>
    ///     <b>V-19's <c>Verified by</c>, half one.</b> Overlapping terms for one school are rejected at
    ///     write time; legacy resolved a date to an arbitrary one of them, silently.
    /// </summary>
    [Fact]
    public async Task Handle_WhenDatesOverlapAnActiveTerm_ThrowsConflict()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await SchoolTermSeed.AddAsync(dbContext, schoolId, name: "Term 1");

        ConflictException exception = await Assert.ThrowsAsync<ConflictException>(
            () => Handle(
                dbContext, FakeCurrentUser.ScopedTo(schoolId), schoolId,
                startDate: new DateOnly(2026, 11, 1), endDate: new DateOnly(2027, 1, 31)));

        Assert.Equal(ErrorCodes.Term.Overlap, exception.ErrorCode);
        Assert.Single(await dbContext.SchoolTerms.AsNoTracking().ToListAsync());
    }

    /// <summary>
    ///     The message names the conflicting term's name and dates — bounded structured values, which
    ///     conventions §2 permits. Without them the client cannot tell which of a school's terms it
    ///     collided with.
    /// </summary>
    [Fact]
    public async Task Handle_WhenDatesOverlap_NamesTheConflictingTermAndItsDates()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await SchoolTermSeed.AddAsync(dbContext, schoolId, name: "Autumn");

        ConflictException exception = await Assert.ThrowsAsync<ConflictException>(
            () => Handle(dbContext, FakeCurrentUser.ScopedTo(schoolId), schoolId));

        Assert.Contains("Autumn", exception.Message, StringComparison.Ordinal);
        Assert.Contains("2026-09-01", exception.Message, StringComparison.Ordinal);
        Assert.Contains("2026-12-20", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Deactivation is how a term is superseded, and this is what makes that true: the parked term
    ///     no longer blocks a replacement over its dates.
    /// </summary>
    [Fact]
    public async Task Handle_WhenDatesOverlapAnInactiveTerm_Succeeds()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await SchoolTermSeed.AddAsync(dbContext, schoolId, name: "Fall (superseded)", isActive: false);

        await Handle(dbContext, FakeCurrentUser.ScopedTo(schoolId), schoolId, name: "Fall");

        Assert.Equal(2, await dbContext.SchoolTerms.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Handle_WhenDatesOverlapAnotherSchoolsTerm_Succeeds()
    {
        Guid schoolId = Guid.NewGuid();
        Guid otherSchoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await SchoolSeed.AddAsync(dbContext, otherSchoolId, name: "Other");
        await SchoolTermSeed.AddAsync(dbContext, otherSchoolId, name: "Their Term 1");

        await Handle(dbContext, FakeCurrentUser.ScopedTo(schoolId), schoolId, name: "Our Term 1");

        Assert.Equal(2, await dbContext.SchoolTerms.AsNoTracking().CountAsync());
    }

    /// <summary>
    ///     <b>Closed bounds, at the handler tier.</b> A term starting the day after an existing one
    ///     ends is the case a real user hits every term, and it must succeed.
    /// </summary>
    [Fact]
    public async Task Handle_WhenDatesAbutAnActiveTerm_Succeeds()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await SchoolTermSeed.AddAsync(dbContext, schoolId, name: "Term 1");

        await Handle(
            dbContext, FakeCurrentUser.ScopedTo(schoolId), schoolId, name: "Term 2",
            startDate: new DateOnly(2026, 12, 21), endDate: new DateOnly(2027, 3, 31));

        Assert.Equal(2, await dbContext.SchoolTerms.AsNoTracking().CountAsync());
    }

    /// <summary>
    ///     The other side of the same boundary: starting <em>on</em> the existing end date is a
    ///     conflict, because both bounds are inclusive. Half-open would let two terms share Dec 20.
    /// </summary>
    [Fact]
    public async Task Handle_WhenDatesStartOnAnActiveTermsLastDay_ThrowsConflict()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await SchoolTermSeed.AddAsync(dbContext, schoolId, name: "Term 1");

        ConflictException exception = await Assert.ThrowsAsync<ConflictException>(
            () => Handle(
                dbContext, FakeCurrentUser.ScopedTo(schoolId), schoolId, name: "Term 2",
                startDate: new DateOnly(2026, 12, 20), endDate: new DateOnly(2027, 3, 31)));

        Assert.Equal(ErrorCodes.Term.Overlap, exception.ErrorCode);
    }

    /// <summary>
    ///     404 before the insert, so <c>TERM.REFERENCE_MISSING</c> stays unreachable outside a race.
    /// </summary>
    [Fact]
    public async Task Handle_WhenSchoolDoesNotExist_ThrowsNotFound()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, FakeCurrentUser.ScopedTo(schoolId), schoolId));

        Assert.Equal(ErrorCodes.School.NotFound, exception.ErrorCode);
        Assert.Empty(await dbContext.SchoolTerms.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Handle_WhenSchoolIsOutsideScope_ThrowsNotFound()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, FakeCurrentUser.ScopedTo(Guid.NewGuid()), schoolId));

        Assert.Equal(ErrorCodes.School.NotFound, exception.ErrorCode);
        Assert.Empty(await dbContext.SchoolTerms.AsNoTracking().ToListAsync());
    }

    /// <summary>
    ///     A non-admin scoped to the school creates a term. F04 has no 403 anywhere; this test is what
    ///     stops one being copied in from the F02 or F03 slice next door.
    /// </summary>
    [Fact]
    public async Task Handle_NeverThrowsForbidden()
    {
        Guid schoolId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await SchoolSeed.AddAsync(dbContext, schoolId);

        GetSchoolTermById.Response response = await Handle(
            dbContext, FakeCurrentUser.ScopedTo(schoolId), schoolId);

        Assert.NotEqual(Guid.Empty, response.Id);
    }

    /// <summary>
    ///     Route values are authoritative (conventions §2), so the JSON body carries no
    ///     <c>schoolId</c>. A body member would let a caller address one school and write to another.
    /// </summary>
    [Fact]
    public void Handle_DoesNotAcceptASchoolIdInTheBody() =>
        Assert.DoesNotContain(
            BodyMembers(), name => name.Equals("SchoolId", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     No <c>isActive</c> on create. It would be a path to an inactive term that is not a
    ///     transition, so it would bypass <c>ActivationPolicy</c> — and an inactive term skips the
    ///     overlap probe, so it would sit waiting to violate the invariant on first reactivation.
    /// </summary>
    [Fact]
    public void Handle_DoesNotAcceptAnActiveFlag()
    {
        Assert.DoesNotContain(BodyMembers(), name => name.Equals("IsActive", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(CreateSchoolTerm.Command).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name),
            name => name.Equals("IsActive", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> BodyMembers() =>
        typeof(CreateSchoolTerm.Body)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name);

    internal static Task<GetSchoolTermById.Response> Handle(
        SparkrockRwcDbContext dbContext,
        FakeCurrentUser currentUser,
        Guid schoolId,
        string name = SchoolTermSeed.DefaultName,
        DateOnly? startDate = null,
        DateOnly? endDate = null)
    {
        CreateSchoolTerm.CommandHandler handler = new(
            dbContext, currentUser, NullLogger<CreateSchoolTerm.CommandHandler>.Instance);

        return handler.Handle(
            new CreateSchoolTerm.Command
            {
                SchoolId = schoolId,
                Name = name,
                StartDate = startDate ?? SchoolTermSeed.DefaultStart,
                EndDate = endDate ?? SchoolTermSeed.DefaultEnd
            },
            CancellationToken.None);
    }
}
