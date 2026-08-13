using domain.Exceptions;
using domain.Schools;
using domain.SchoolTerms;
using domain.Students;
using features.Schools;
using features.tests.Fakes;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace features.tests.Schools;

public sealed class DeactivateSchoolHandlerTests
{
    [Fact]
    public async Task Handle_SetsIsActiveToFalse()
    {
        Guid schoolId = Guid.NewGuid();
        FakeCurrentUser admin = FakeCurrentUser.SystemAdmin();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: admin);
        await SchoolSeed.AddAsync(dbContext, schoolId);

        await Handle(dbContext, admin, schoolId);

        Assert.False(Assert.Single(await dbContext.Schools.AsNoTracking().ToListAsync()).IsActive);
    }

    /// <summary>
    ///     <c>DELETE</c> deactivates and never removes. The audit interceptor throws
    ///     <see cref="InvalidOperationException" /> for <c>EntityState.Deleted</c> on anything that is
    ///     not soft-deletable (DEC-20), which would surface as a 500 — and EF's default cascade on a
    ///     required relationship would physically delete the school's students. This test is what
    ///     catches a handler reaching for <c>Remove</c>.
    /// </summary>
    [Fact]
    public async Task Handle_DoesNotRemoveTheRow()
    {
        Guid schoolId = Guid.NewGuid();
        FakeCurrentUser admin = FakeCurrentUser.SystemAdmin();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: admin);
        await SchoolSeed.AddAsync(dbContext, schoolId, name: "Still Here");

        await Handle(dbContext, admin, schoolId);

        School persisted = Assert.Single(await dbContext.Schools.AsNoTracking().ToListAsync());
        Assert.Equal(schoolId, persisted.Id);
        Assert.Equal("Still Here", persisted.Name);
    }

    /// <summary>
    ///     Step 5 of the shared <c>DELETE</c> contract writes nothing. A no-op
    ///     <c>SaveChangesAsync</c> stamps <c>ModifiedAt</c> through the interceptor and reports a
    ///     change that did not happen, making <c>lastUpdatedAt</c> lie.
    /// </summary>
    [Fact]
    public async Task Handle_WhenAlreadyInactive_DoesNotWrite()
    {
        Guid schoolId = Guid.NewGuid();
        FakeCurrentUser admin = FakeCurrentUser.SystemAdmin();
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock, admin);
        School school = await SchoolSeed.AddAsync(dbContext, schoolId, isActive: false);
        Assert.Null(school.ModifiedAt);

        clock.Advance(TimeSpan.FromHours(5));

        await Handle(dbContext, admin, schoolId);

        Assert.Null(Assert.Single(await dbContext.Schools.AsNoTracking().ToListAsync()).ModifiedAt);
        Assert.False(dbContext.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task Handle_WhenSchoolDoesNotExist_ThrowsNotFound()
    {
        FakeCurrentUser admin = FakeCurrentUser.SystemAdmin();
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: admin);

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, admin, Guid.NewGuid()));

        Assert.Equal(ErrorCodes.School.NotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WhenSchoolIsOutsideScope_ThrowsNotFound()
    {
        Guid schoolId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(Guid.NewGuid());

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, caller, schoolId));

        Assert.Equal(ErrorCodes.School.NotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WhenCallerIsNotSystemAdmin_ThrowsForbidden()
    {
        Guid schoolId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);

        ForbiddenException exception = await Assert.ThrowsAsync<ForbiddenException>(
            () => Handle(dbContext, caller, schoolId));

        Assert.Equal(ErrorCodes.System.Forbidden, exception.ErrorCode);
        Assert.True(Assert.Single(await dbContext.Schools.AsNoTracking().ToListAsync()).IsActive);
    }

    /// <summary>
    ///     The refusal does not depend on the row's current state. The alternative — 204 when already
    ///     inactive, 403 when active — turns the status code into a state oracle for an unprivileged
    ///     caller, so the idempotence promise holds only for callers who may perform the operation at
    ///     all. This test exists so a later reader does not "fix" the ordering.
    /// </summary>
    [Fact]
    public async Task Handle_WhenCallerIsNotSystemAdminAndSchoolIsAlreadyInactive_ThrowsForbidden()
    {
        Guid schoolId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId, isActive: false);

        ForbiddenException exception = await Assert.ThrowsAsync<ForbiddenException>(
            () => Handle(dbContext, caller, schoolId));

        Assert.Equal(ErrorCodes.System.Forbidden, exception.ErrorCode);
    }

    /// <summary>
    ///     Deactivating a school cascades to nothing. This is the assertion that would have caught EF's
    ///     default <c>Cascade</c> had the model not pinned <c>Restrict</c>.
    /// </summary>
    [Fact]
    public async Task Handle_LeavesStudentsAndTermsUntouched()
    {
        Guid schoolId = Guid.NewGuid();
        FakeCurrentUser admin = FakeCurrentUser.SystemAdmin();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: admin);
        await SchoolSeed.AddAsync(dbContext, schoolId);

        dbContext.Students.Add(new Student
        {
            Id = Guid.NewGuid(),
            SchoolId = schoolId,
            FirstName = "Ada",
            LastName = "Lovelace"
        });

        dbContext.SchoolTerms.Add(new SchoolTerm
        {
            Id = Guid.NewGuid(),
            SchoolId = schoolId,
            Name = "Autumn",
            StartDate = new DateOnly(2026, 9, 1),
            EndDate = new DateOnly(2026, 12, 18)
        });

        await dbContext.SaveChangesAsync(CancellationToken.None);

        await Handle(dbContext, admin, schoolId);

        Student student = Assert.Single(await dbContext.Students.AsNoTracking().ToListAsync());
        SchoolTerm term = Assert.Single(await dbContext.SchoolTerms.AsNoTracking().ToListAsync());
        Assert.True(student.IsActive);
        Assert.True(term.IsActive);
        Assert.Null(student.ModifiedAt);
        Assert.Null(term.ModifiedAt);
    }

    private static Task Handle(SparkrockRwcDbContext dbContext, FakeCurrentUser currentUser, Guid schoolId)
    {
        DeactivateSchool.CommandHandler handler = new(
            dbContext, currentUser, NullLogger<DeactivateSchool.CommandHandler>.Instance);

        return handler.Handle(new DeactivateSchool.Command { SchoolId = schoolId }, CancellationToken.None);
    }
}
