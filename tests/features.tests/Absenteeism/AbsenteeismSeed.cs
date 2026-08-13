using domain.Attendance;
using domain.ValueObjects;
using features.tests.AttendanceCodes;
using infra.persistence.postgre;

namespace features.tests.Absenteeism;

/// <summary>
///     Seeds the two rows F09 reads — the summary it reports and the attendance rows the
///     cross-school marker is computed from — through the real context, so the audit interceptor
///     stamps them.
/// </summary>
/// <remarks>
///     Audit fields are never hand-set (DEC-21). A test that needs a particular <c>CreatedAt</c> or
///     <c>ModifiedAt</c> advances the <c>FakeTimeProvider</c> the context was built with.
/// </remarks>
internal static class AbsenteeismSeed
{
    public static async Task<StudentAttendanceSummary> SummaryAsync(
        SparkrockRwcDbContext dbContext,
        Guid studentId,
        Guid schoolId,
        int schoolYearStart,
        int totalAbsences)
    {
        StudentAttendanceSummary summary = new()
        {
            Id = Guid.NewGuid(),
            StudentId = studentId,
            SchoolId = schoolId,
            SchoolYearStart = SchoolYear.FromStartYear(schoolYearStart),
            TotalAbsences = totalAbsences
        };

        dbContext.StudentAttendanceSummaries.Add(summary);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        return summary;
    }

    /// <summary>
    ///     One attendance row. <paramref name="schoolId" /> is the school that recorded it, which is
    ///     the whole point of the cross-school marker — it is not necessarily the student's current
    ///     school.
    /// </summary>
    public static async Task<StudentAttendance> AttendanceAsync(
        SparkrockRwcDbContext dbContext,
        Guid studentId,
        Guid schoolId,
        DateOnly attendDate,
        bool isAbsent = true)
    {
        Guid codeId = (await AttendanceCodeSeed.AddAsync(
            dbContext,
            value: isAbsent ? "A" : "P",
            description: isAbsent ? "Absent" : "Present",
            isAbsent: isAbsent)).Id;

        StudentAttendance attendance = new()
        {
            Id = Guid.NewGuid(),
            StudentId = studentId,
            SchoolId = schoolId,
            AttendDate = attendDate,
            AttendanceCodeId = codeId,
            AttendCode = isAbsent ? "A" : "P",
            AttendCodeDescription = isAbsent ? "Absent" : "Present",
            IsAbsent = isAbsent,
            IsExcused = false
        };

        dbContext.StudentAttendances.Add(attendance);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        return attendance;
    }
}
