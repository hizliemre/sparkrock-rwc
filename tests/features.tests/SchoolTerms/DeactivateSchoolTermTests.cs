using domain.Exceptions;
using domain.SchoolTerms;
using features.SchoolTerms;
using features.tests.Fakes;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace features.tests.SchoolTerms;

public sealed class DeactivateSchoolTermHandlerTests
{
    [Fact]
    public async Task Handle_SetsIsActiveToFalse()
    {
        Guid schoolId = Guid.NewGuid();
        Guid termId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolTermSeed.AddAsync(dbContext, schoolId, termId);

        await Handle(dbContext, caller, schoolId, termId);

        Assert.False(Assert.Single(await dbContext.SchoolTerms.AsNoTracking().ToListAsync()).IsActive);
    }

    /// <summary>
    ///     <c>DELETE</c> deactivates and never removes. <see cref="SchoolTerm" /> derives from
    ///     <c>BaseEntity</c>, and the audit interceptor throws for <c>EntityState.Deleted</c> on
    ///     anything that is not soft-deletable (DEC-20) — a 500, not a delete. Recorded attendance
    ///     still points at the term through a nullable <c>StudentAttendance.TermId</c>.
    /// </summary>
    [Fact]
    public async Task Handle_DoesNotRemoveTheRow()
    {
        Guid schoolId = Guid.NewGuid();
        Guid termId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolTermSeed.AddAsync(dbContext, schoolId, termId, name: "Still Here");

        await Handle(dbContext, caller, schoolId, termId);

        SchoolTerm persisted = Assert.Single(await dbContext.SchoolTerms.AsNoTracking().ToListAsync());
        Assert.Equal(termId, persisted.Id);
        Assert.Equal("Still Here", persisted.Name);
        Assert.Equal(SchoolTermSeed.DefaultStart, persisted.StartDate);
        Assert.Equal(SchoolTermSeed.DefaultEnd, persisted.EndDate);
    }

    /// <summary>
    ///     Step 5 of the shared <c>DELETE</c> contract writes nothing. A no-op
    ///     <c>SaveChangesAsync</c> stamps <c>ModifiedAt</c> through the interceptor and reports a
    ///     change that did not happen, making <c>lastUpdatedAt</c> lie.
    /// </summary>
    /// <remarks>
    ///     <b>The <c>ModifiedAt</c> and <c>HasChanges</c> assertions cannot fail on their own.</b>
    ///     Assigning <c>IsActive = false</c> to an already-inactive row leaves the change tracker
    ///     empty, so an unguarded save writes nothing anyway and both hold whether or not the guard
    ///     exists. They stay because they state the contract; the assertion that actually fires is the
    ///     absent log line, which is the one effect an unguarded handler still produces.
    /// </remarks>
    [Fact]
    public async Task Handle_WhenAlreadyInactive_DoesNotWrite()
    {
        Guid schoolId = Guid.NewGuid();
        Guid termId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();
        RecordingLogger<DeactivateSchoolTerm.CommandHandler> logger = new();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock, caller);
        SchoolTerm term = await SchoolTermSeed.AddAsync(dbContext, schoolId, termId, isActive: false);
        Assert.Null(term.ModifiedAt);

        clock.Advance(TimeSpan.FromHours(5));

        await Handle(dbContext, caller, schoolId, termId, logger);

        Assert.Null(Assert.Single(await dbContext.SchoolTerms.AsNoTracking().ToListAsync()).ModifiedAt);
        Assert.False(dbContext.ChangeTracker.HasChanges());
        Assert.Empty(logger.Events);
    }

    /// <summary>The counterpart: a real transition does log, so the assertion above is not vacuous.</summary>
    [Fact]
    public async Task Handle_WhenTransitionHappens_LogsOnce()
    {
        Guid schoolId = Guid.NewGuid();
        Guid termId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);
        RecordingLogger<DeactivateSchoolTerm.CommandHandler> logger = new();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolTermSeed.AddAsync(dbContext, schoolId, termId);

        await Handle(dbContext, caller, schoolId, termId, logger);

        Assert.Equal(1402, Assert.Single(logger.Events).Id);
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
        await SchoolTermSeed.AddAsync(dbContext, owningSchoolId, termId);

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, caller, addressedSchoolId, termId));

        Assert.Equal(ErrorCodes.Term.NotFound, exception.ErrorCode);
        Assert.True(Assert.Single(await dbContext.SchoolTerms.AsNoTracking().ToListAsync()).IsActive);
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
        Assert.True(Assert.Single(await dbContext.SchoolTerms.AsNoTracking().ToListAsync()).IsActive);
    }

    /// <summary>
    ///     A term is one school's calendar, so school scope is the whole privilege
    ///     (<c>ActivationPrivilege.SchoolScope</c>) and <b>no 403 exists anywhere in this feature</b>.
    ///     A non-admin scoped to the school must succeed. This test is what stops a
    ///     <c>SystemAdmin</c> privilege being copied in from the F02 or F03 slice next door.
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

        await Handle(dbContext, nonAdmin, schoolId, termId);

        Assert.False(Assert.Single(await dbContext.SchoolTerms.AsNoTracking().ToListAsync()).IsActive);
    }

    /// <summary>
    ///     The same call on an already-inactive term is still a 204 and still not a 403 — the two
    ///     interact, because <c>ActivationPolicy.Apply</c> checks privilege <em>before</em> the
    ///     already-in-that-state short-circuit. With <c>SchoolScope</c> there is no privilege to fail,
    ///     so the order is unobservable here; the test pins that it stays that way.
    /// </summary>
    [Fact]
    public async Task Handle_WhenAlreadyInactiveAndCallerIsNotAdmin_NeverThrowsForbidden()
    {
        Guid schoolId = Guid.NewGuid();
        Guid termId = Guid.NewGuid();
        FakeCurrentUser nonAdmin = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: nonAdmin);
        await SchoolTermSeed.AddAsync(dbContext, schoolId, termId, isActive: false);

        await Handle(dbContext, nonAdmin, schoolId, termId);

        Assert.False(Assert.Single(await dbContext.SchoolTerms.AsNoTracking().ToListAsync()).IsActive);
    }

    /// <summary>
    ///     Deactivating a term does not touch another school's calendar, and does not touch this
    ///     school's other terms.
    /// </summary>
    [Fact]
    public async Task Handle_LeavesOtherTermsUntouched()
    {
        Guid schoolId = Guid.NewGuid();
        Guid termId = Guid.NewGuid();
        Guid siblingTermId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolTermSeed.AddAsync(dbContext, schoolId, termId);
        await SchoolTermSeed.AddAsync(
            dbContext, schoolId, siblingTermId, "Term 2",
            new DateOnly(2027, 1, 5), new DateOnly(2027, 3, 31));

        await Handle(dbContext, caller, schoolId, termId);

        SchoolTerm sibling = await dbContext.SchoolTerms.AsNoTracking()
            .SingleAsync(term => term.Id == siblingTermId);

        Assert.True(sibling.IsActive);
        Assert.Null(sibling.ModifiedAt);
    }

    private static Task Handle(
        SparkrockRwcDbContext dbContext,
        FakeCurrentUser currentUser,
        Guid schoolId,
        Guid termId,
        ILogger<DeactivateSchoolTerm.CommandHandler>? logger = null)
    {
        DeactivateSchoolTerm.CommandHandler handler = new(
            dbContext, currentUser, logger ?? NullLogger<DeactivateSchoolTerm.CommandHandler>.Instance);

        return handler.Handle(
            new DeactivateSchoolTerm.Command { SchoolId = schoolId, TermId = termId },
            CancellationToken.None);
    }
}
