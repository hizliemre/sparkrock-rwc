using domain.Alerts;
using domain.ValueObjects;
using infra.persistence.postgre;

namespace features.tests.Alerts;

/// <summary>
///     Inserts a <see cref="StudentAlert" /> through the real context, so the audit interceptor
///     stamps it.
/// </summary>
/// <remarks>
///     Audit fields are never hand-set (DEC-21). <c>RaisedAt</c> is projected from <c>CreatedAt</c>
///     (spec §2, risk R-3), so a test that needs two alerts in a known order advances the
///     <c>FakeTimeProvider</c> the context was built with between calls — one
///     <c>SaveChangesAsync</c> per row.
///     <para>
///         The four resolution columns are <b>not</b> audit fields and are settable: F07 writes them
///         directly on the auto-resolve path. <see cref="ResolvedAsync" /> writes all of
///         <c>ResolvedAt</c> and <c>ResolutionSource</c> together, because
///         <c>ck_student_alerts_resolution_consistent</c> makes a half-written resolution
///         unrepresentable on a real database and a seed that produced one would pass at the handler
///         tier and fail only in the container.
///     </para>
/// </remarks>
internal static class AlertSeed
{
    public const int DefaultSchoolYearStart = 2026;

    public const int DefaultAbsenceCount = 11;

    public const int DefaultThresholdAtRaise = 10;

    public static async Task<StudentAlert> OpenAsync(
        SparkrockRwcDbContext dbContext,
        Guid studentId,
        Guid schoolId,
        Guid? id = null,
        int schoolYearStart = DefaultSchoolYearStart,
        int absenceCount = DefaultAbsenceCount,
        int thresholdAtRaise = DefaultThresholdAtRaise)
    {
        StudentAlert alert = new()
        {
            Id = id ?? Guid.NewGuid(),
            StudentId = studentId,
            SchoolId = schoolId,
            AlertType = AlertType.ChronicAbsence,
            SchoolYearStart = SchoolYear.FromStartYear(schoolYearStart),
            AbsenceCount = absenceCount,
            ThresholdAtRaise = thresholdAtRaise
        };

        dbContext.StudentAlerts.Add(alert);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        return alert;
    }

    /// <summary>An already-closed episode, as F07's auto-resolve or a prior manual resolution left it.</summary>
    public static async Task<StudentAlert> ResolvedAsync(
        SparkrockRwcDbContext dbContext,
        Guid studentId,
        Guid schoolId,
        Guid? id = null,
        int schoolYearStart = DefaultSchoolYearStart,
        int absenceCount = DefaultAbsenceCount,
        int thresholdAtRaise = DefaultThresholdAtRaise,
        ResolutionSource resolutionSource = ResolutionSource.Manual,
        string? resolutionReason = "Seeded resolution.",
        DateTimeOffset? resolvedAt = null,
        Guid? resolvedBy = null)
    {
        StudentAlert alert = await OpenAsync(
            dbContext, studentId, schoolId, id, schoolYearStart, absenceCount, thresholdAtRaise);

        alert.ResolvedAt = resolvedAt ?? InMemoryDbContextFactory.DefaultNow;
        alert.ResolvedBy = resolvedBy ?? Guid.Parse("22222222-2222-2222-2222-222222222222");
        alert.ResolutionSource = resolutionSource;
        alert.ResolutionReason = resolutionReason;

        await dbContext.SaveChangesAsync(CancellationToken.None);

        return alert;
    }
}
