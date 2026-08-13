using domain.Attendance;
using domain.AttendanceCodes;
using domain.Schools;
using domain.Students;
using infra.persistence.postgre;

namespace features.tests.AttendanceSubmissions;

/// <summary>
///     Rows for the two F11 slices.
/// </summary>
/// <remarks>
///     <c>SubmittedAt</c> is always passed in explicitly and never taken from the clock. It is the
///     keyset ordering column, so a fixture that let it drift would make "two rows tie" impossible to
///     construct — and the tie is the entire case O-06 exists to handle.
/// </remarks>
internal static class SubmissionLogSeed
{
    /// <summary>The instant every fixture is written relative to. Arbitrary, but fixed.</summary>
    public static readonly DateTimeOffset BaseInstant = new(2026, 9, 14, 8, 31, 0, TimeSpan.Zero);

    public static readonly DateOnly BaseDate = new(2026, 9, 14);

    public static School School(Guid id) => new()
    {
        Id = id,
        Name = "Submission Log School",
        TimeZoneId = "America/Toronto"
    };

    public static Student Student(Guid id, Guid schoolId, string firstName, string lastName) => new()
    {
        Id = id,
        SchoolId = schoolId,
        FirstName = firstName,
        LastName = lastName
    };

    public static AttendanceCode Code(Guid id) => new()
    {
        Id = id,
        Value = "A",
        Description = "Absent",
        IsAbsent = true,
        IsExcused = false
    };

    public static AttendanceSubmissionLog Log(
        Guid id,
        Guid schoolId,
        DateOnly attendDate,
        DateTimeOffset submittedAt,
        int recordCount = 1,
        Guid submittedBy = default,
        string? idempotencyKey = null) => new()
    {
        Id = id,
        SchoolId = schoolId,
        AttendDate = attendDate,
        SubmittedAt = submittedAt,
        RecordCount = recordCount,
        SubmittedBy = submittedBy,
        IdempotencyKey = idempotencyKey
    };

    public static StudentAttendance Attendance(
        Guid id,
        Guid studentId,
        Guid schoolId,
        Guid attendanceCodeId,
        DateOnly attendDate,
        Guid? submissionId,
        string? notes = null,
        int? minutesLate = null) => new()
    {
        Id = id,
        StudentId = studentId,
        SchoolId = schoolId,
        AttendanceCodeId = attendanceCodeId,
        AttendDate = attendDate,
        SubmissionId = submissionId,
        AttendCode = "A",
        AttendCodeDescription = "Absent",
        IsAbsent = true,
        IsExcused = false,
        MinutesLate = minutesLate,
        Notes = notes
    };

    /// <summary>
    ///     <paramref name="count" /> ids in ascending <see cref="Comparer{T}" /> order.
    /// </summary>
    /// <remarks>
    ///     Only meaningful at the handler tier. <c>Comparer&lt;Guid&gt;.Default</c> is what EF InMemory
    ///     sorts by, and it <b>disagrees</b> with Postgres, which compares a <c>uuid</c> as sixteen
    ///     big-endian bytes. Every assertion that depends on this ordering is therefore handler-tier
    ///     only, and the integration tier asserts the provider-agnostic property instead — a paged
    ///     traversal visits the same rows, in the same order, as an unpaged one.
    /// </remarks>
    public static Guid[] AscendingIds(int count)
    {
        Guid[] ids = new Guid[count];

        for (int index = 0; index < count; index++)
            ids[index] = Guid.NewGuid();

        Array.Sort(ids);

        return ids;
    }

    public static async Task SaveAsync(SparkrockRwcDbContext dbContext, params AttendanceSubmissionLog[] logs)
    {
        dbContext.AttendanceSubmissionLogs.AddRange(logs);

        await dbContext.SaveChangesAsync(CancellationToken.None);
    }
}
