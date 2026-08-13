using domain;
using domain.Alerts;
using domain.Attendance;
using domain.AttendanceCodes;
using domain.Schools;
using domain.SchoolTerms;
using domain.Students;
using infra.persistence.postgre;
using infra.persistence.sql;
using Microsoft.EntityFrameworkCore;

namespace features.tests.Attendance;

/// <summary>
///     An <see cref="IDbContext" /> that counts saves and can be told to fail one.
/// </summary>
/// <remarks>
///     Conventions §6 bans mocking packages, so this is a hand-written decorator. It exists for the
///     two things the handler tier <em>can</em> honestly assert about the retry loop: that a save
///     happens exactly once when nothing races, and that a discarded attempt does not soft-delete the
///     rows it added.
///     <para>
///         <b>It cannot make a race real.</b> VC-35: EF InMemory builds the <c>uint</c>/<c>xmin</c>
///         token but never populates it, so the token stays zero and every concurrency check passes. A
///         failure injected here is a hand-thrown exception, not a lost update — it exercises the
///         handler's recovery <em>path</em> and proves nothing about the mechanism recovering from a
///         real one. The three races, attempt exhaustion and atomicity are integration-tier and
///         nowhere else.
///     </para>
/// </remarks>
internal sealed class CountingDbContext(SparkrockRwcDbContext inner) : IDbContext
{
    /// <summary>Given the 1-based save number, returns an exception to throw instead of saving.</summary>
    public Func<int, Exception?>? FailOn { get; init; }

    /// <summary>Runs after a save number is chosen and before the exception is thrown, if any.</summary>
    public Action<int>? BeforeSave { get; init; }

    public int SaveChangesCalls { get; private set; }

    public DbSet<School> Schools
    {
        get => inner.Schools;
        set => inner.Schools = value;
    }

    public DbSet<Student> Students
    {
        get => inner.Students;
        set => inner.Students = value;
    }

    public DbSet<AttendanceCode> AttendanceCodes
    {
        get => inner.AttendanceCodes;
        set => inner.AttendanceCodes = value;
    }

    public DbSet<SchoolTerm> SchoolTerms
    {
        get => inner.SchoolTerms;
        set => inner.SchoolTerms = value;
    }

    public DbSet<StudentAttendance> StudentAttendances
    {
        get => inner.StudentAttendances;
        set => inner.StudentAttendances = value;
    }

    public DbSet<StudentAttendanceSummary> StudentAttendanceSummaries
    {
        get => inner.StudentAttendanceSummaries;
        set => inner.StudentAttendanceSummaries = value;
    }

    public DbSet<StudentAlert> StudentAlerts
    {
        get => inner.StudentAlerts;
        set => inner.StudentAlerts = value;
    }

    public DbSet<AttendanceSubmissionLog> AttendanceSubmissionLogs
    {
        get => inner.AttendanceSubmissionLogs;
        set => inner.AttendanceSubmissionLogs = value;
    }

    public DbSet<TestEntity> TestEntities
    {
        get => inner.TestEntities;
        set => inner.TestEntities = value;
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveChangesCalls++;

        BeforeSave?.Invoke(SaveChangesCalls);

        Exception? failure = FailOn?.Invoke(SaveChangesCalls);

        return failure is null
            ? inner.SaveChangesAsync(cancellationToken)
            : Task.FromException<int>(failure);
    }

    /// <summary>
    ///     A <see cref="domain.Exceptions.ConcurrencyConflictException" /> naming a retryable
    ///     constraint, with an empty <c>Entries</c> list.
    /// </summary>
    /// <remarks>
    ///     Empty on purpose. It forces recovery through the half that does <b>not</b> depend on
    ///     <c>DbUpdateException.Entries</c> — the handler's own list of what it added — which plan R-2
    ///     records as load-bearing rather than belt-and-braces, because VC-29 pins <c>Entries</c> only
    ///     for a three-entity batch and never for the attendance first-insert inside a large one.
    /// </remarks>
    public static Exception RetryableConflict(string constraintName, string? errorCode = null) =>
        new domain.Exceptions.ConcurrencyConflictException(
            constraintName,
            errorCode ?? domain.Exceptions.ErrorCodes.Attendance.ConcurrentSubmission,
            "Injected.",
            new DbUpdateException("Injected.", (Exception?)null));
}
