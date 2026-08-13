using domain.Attendance;
using domain.Exceptions;
using domain.Students;
using features.Students;
using features.tests.Fakes;
using features.tests.Schools;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace features.tests.Students;

public sealed class DeactivateStudentHandlerTests
{
    [Fact]
    public async Task Handle_SetsIsActiveToFalse()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        await Handle(dbContext, caller, schoolId, studentId);

        Assert.False(Assert.Single(await dbContext.Students.AsNoTracking().ToListAsync()).IsActive);
    }

    /// <summary>
    ///     <c>DELETE</c> deactivates and never removes. <c>Student</c> derives from <c>BaseEntity</c>,
    ///     so the audit interceptor throws for <c>EntityState.Deleted</c> (DEC-20) and it would surface
    ///     as a 500 rather than a delete.
    ///     <para>
    ///         This is also the test that keeps <c>DELETE</c> from being mistaken for erasure. DEC-19
    ///         is explicit that a flag flip presented as deletion misleads a records-destruction
    ///         workflow, and the audited purge that would satisfy one has no owner (O-20).
    ///     </para>
    /// </summary>
    [Fact]
    public async Task Handle_DoesNotRemoveTheRow()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId, "Still", "Here");

        await Handle(dbContext, caller, schoolId, studentId);

        Student persisted = Assert.Single(await dbContext.Students.AsNoTracking().ToListAsync());
        Assert.Equal(studentId, persisted.Id);
        Assert.Equal("Still", persisted.FirstName);
        Assert.Equal("Here", persisted.LastName);
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
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock, caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        Student seeded = await StudentSeed.AddAsync(dbContext, schoolId, studentId, isActive: false);
        Assert.Null(seeded.ModifiedAt);

        clock.Advance(TimeSpan.FromHours(5));

        await Handle(dbContext, caller, schoolId, studentId);

        Assert.Null(Assert.Single(await dbContext.Students.AsNoTracking().ToListAsync()).ModifiedAt);
        Assert.False(dbContext.ChangeTracker.HasChanges());
    }

    /// <summary>
    ///     The companion to <see cref="Handle_WhenAlreadyInactive_DoesNotWrite" />, and the one that
    ///     actually covers the <c>ActivationPolicy.Apply</c> short-circuit.
    /// </summary>
    /// <remarks>
    ///     Found by mutation: replacing the <c>Apply</c> call with a direct
    ///     <c>student.IsActive = false</c> and an unconditional <c>SaveChangesAsync</c> left all ninety
    ///     F05 tests green. EF's change tracker treats assigning a value a property already holds as no
    ///     change, so <c>ModifiedAt</c> stays null and <c>HasChanges()</c> stays false either way — the
    ///     assertion above is satisfied by the provider, not by the handler. The log line is the
    ///     observable difference: the bypassing handler announces a deactivation that did not happen.
    ///     <para>
    ///         This is also the O-12 shape the spec forbids by name: "a slice that assigned
    ///         <c>student.IsActive</c> directly would be the thing O-12 is about, whether or not a check
    ///         exists today".
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task Handle_WhenAlreadyInactive_DoesNotLogADeactivation()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);
        RecordingLogger<DeactivateStudent.CommandHandler> logger = new();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId, isActive: false);

        DeactivateStudent.CommandHandler handler = new(dbContext, caller, logger);
        await handler.Handle(
            new DeactivateStudent.Command { SchoolId = schoolId, StudentId = studentId },
            CancellationToken.None);

        Assert.Empty(logger.EventIds);
    }

    /// <summary>
    ///     The positive half, so the assertion above cannot pass because the slice never logs at all.
    /// </summary>
    [Fact]
    public async Task Handle_WhenActive_LogsTheDeactivationOnce()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);
        RecordingLogger<DeactivateStudent.CommandHandler> logger = new();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        DeactivateStudent.CommandHandler handler = new(dbContext, caller, logger);
        await handler.Handle(
            new DeactivateStudent.Command { SchoolId = schoolId, StudentId = studentId },
            CancellationToken.None);

        Assert.Equal(1202, Assert.Single(logger.EventIds));
    }

    [Fact]
    public async Task Handle_WhenStudentDoesNotExist_ThrowsNotFound()
    {
        Guid schoolId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, caller, schoolId, Guid.NewGuid()));

        Assert.Equal(ErrorCodes.Student.NotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WhenStudentBelongsToAnotherSchool_ThrowsNotFound()
    {
        Guid addressedSchoolId = Guid.NewGuid();
        Guid otherSchoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(addressedSchoolId, otherSchoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, addressedSchoolId, name: "Addressed");
        await SchoolSeed.AddAsync(dbContext, otherSchoolId, name: "Other");
        await StudentSeed.AddAsync(dbContext, otherSchoolId, studentId);

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, caller, addressedSchoolId, studentId));

        Assert.Equal(ErrorCodes.Student.NotFound, exception.ErrorCode);
        Assert.True(Assert.Single(await dbContext.Students.AsNoTracking().ToListAsync()).IsActive);
    }

    [Fact]
    public async Task Handle_WhenSchoolIsOutsideScope_ThrowsNotFound()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(Guid.NewGuid());

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, caller, schoolId, studentId));

        Assert.Equal(ErrorCodes.School.NotFound, exception.ErrorCode);
        Assert.True(Assert.Single(await dbContext.Students.AsNoTracking().ToListAsync()).IsActive);
    }

    /// <summary>
    ///     No 403 is reachable in F05, on either the active or the already-inactive path. DEC-20
    ///     requires school scope and no more for a student, so an in-scope non-admin deactivating is
    ///     the ordinary case rather than the privileged one.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handle_NeverThrowsForbidden(bool startsActive)
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        Assert.False(caller.IsSystemAdmin);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId, isActive: startsActive);

        await Handle(dbContext, caller, schoolId, studentId);

        Assert.False(Assert.Single(await dbContext.Students.AsNoTracking().ToListAsync()).IsActive);
    }

    /// <summary>
    ///     DEC-19: deactivating a student removes them from nothing. Their attendance history stays
    ///     readable, and F07's save pipeline deliberately does not check whether a student is active —
    ///     legacy accepted attendance for inactive students and that is a preserved behaviour.
    /// </summary>
    [Fact]
    public async Task Handle_LeavesAttendanceHistoryReadable()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        dbContext.StudentAttendances.Add(new StudentAttendance
        {
            Id = Guid.NewGuid(),
            StudentId = studentId,
            SchoolId = schoolId,
            AttendDate = new DateOnly(2026, 9, 14),
            AttendanceCodeId = Guid.NewGuid(),
            AttendCode = "A",
            AttendCodeDescription = "Absent",
            IsAbsent = true,
            IsExcused = false
        });

        await dbContext.SaveChangesAsync(CancellationToken.None);

        await Handle(dbContext, caller, schoolId, studentId);

        StudentAttendance attendance = Assert.Single(
            await dbContext.StudentAttendances.AsNoTracking().ToListAsync());

        Assert.Equal(studentId, attendance.StudentId);
        Assert.False(attendance.IsDeleted);
        Assert.False(Assert.Single(await dbContext.Students.AsNoTracking().ToListAsync()).IsActive);
    }

    private static Task Handle(
        SparkrockRwcDbContext dbContext,
        FakeCurrentUser currentUser,
        Guid schoolId,
        Guid studentId)
    {
        DeactivateStudent.CommandHandler handler = new(
            dbContext, currentUser, NullLogger<DeactivateStudent.CommandHandler>.Instance);

        return handler.Handle(
            new DeactivateStudent.Command { SchoolId = schoolId, StudentId = studentId },
            CancellationToken.None);
    }
}
