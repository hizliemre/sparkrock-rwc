using domain.Attendance;
using domain.SchoolTerms;
using infra.persistence.postgre;

namespace features.tests.Attendance;

/// <summary>
///     Inserts a <see cref="StudentAttendance" /> through the real context, so the audit interceptor
///     stamps it and the soft-delete rewrite applies on removal.
/// </summary>
/// <remarks>
///     Audit fields are never hand-set (DEC-21) — a test that needs a particular <c>CreatedAt</c> or
///     <c>ModifiedAt</c> advances the <c>FakeTimeProvider</c> instead.
///     <para>
///         The snapshot columns are written verbatim rather than copied from an
///         <c>AttendanceCode</c> row. F08 must project the snapshot and never the code table, and a
///         seed that derived one from the other would make the two indistinguishable.
///     </para>
/// </remarks>
internal static class StudentAttendanceSeed
{
    public const string DefaultAttendCode = "A";

    public const string DefaultAttendCodeDescription = "Absent — unexcused";

    public static async Task<StudentAttendance> AddAsync(
        SparkrockRwcDbContext dbContext,
        Guid studentId,
        Guid schoolId,
        DateOnly attendDate,
        Guid? id = null,
        Guid? attendanceCodeId = null,
        Guid? termId = null,
        string attendCode = DefaultAttendCode,
        string attendCodeDescription = DefaultAttendCodeDescription,
        bool isAbsent = true,
        bool isExcused = false,
        int? minutesLate = null,
        string? notes = null)
    {
        StudentAttendance attendance = new()
        {
            Id = id ?? Guid.NewGuid(),
            StudentId = studentId,
            SchoolId = schoolId,
            AttendDate = attendDate,
            TermId = termId,
            AttendanceCodeId = attendanceCodeId ?? Guid.NewGuid(),
            AttendCode = attendCode,
            AttendCodeDescription = attendCodeDescription,
            IsAbsent = isAbsent,
            IsExcused = isExcused,
            MinutesLate = minutesLate,
            Notes = notes
        };

        dbContext.StudentAttendances.Add(attendance);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        return attendance;
    }

    /// <summary>
    ///     Inserts a <see cref="SchoolTerm" />. Local to F08's tests because F04 has not shipped a
    ///     shared seed yet; the moment it does, this should be deleted in favour of it.
    /// </summary>
    public static async Task<SchoolTerm> AddTermAsync(
        SparkrockRwcDbContext dbContext,
        Guid schoolId,
        DateOnly startDate,
        DateOnly endDate,
        Guid? id = null,
        string name = "Fall Term")
    {
        SchoolTerm term = new()
        {
            Id = id ?? Guid.NewGuid(),
            SchoolId = schoolId,
            Name = name,
            StartDate = startDate,
            EndDate = endDate
        };

        dbContext.SchoolTerms.Add(term);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        return term;
    }
}
