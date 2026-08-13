using domain.Alerts;
using domain.Students;
using features.Attendance;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;

namespace features.tests.Attendance;

/// <summary>
///     DEC-18's alert lifecycle as F07 executes it, and DEC-16's four-column keys.
/// </summary>
/// <remarks>
///     The predicates themselves are <c>domain/Alerts/AlertRules.cs</c> and are tested there. What is
///     under test here is the <em>arguments</em>: getting one key wrong is what DEC-16 records as a
///     safeguarding failure, and every one of those mistakes compiles.
///     <para>
///         V-08's Feature column reads <c>F01b, F10</c>. Auto-resolve first <em>executes</em> here —
///         F10 owns manual resolution — and plan conflict 5 asks for F07 to be added to that row.
///         These are the tests that would satisfy it.
///     </para>
/// </remarks>
public sealed class SaveDailyAttendanceAlertTests
{
    /// <summary><c>&gt;=</c>, not <c>&gt;</c>: at exactly the threshold the episode opens.</summary>
    [Fact]
    public async Task Handle_WhenTotalReachesThresholdAndNoEpisodeIsOpen_RaisesAnAlert()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        await AddAbsencesAsync(fixture, student.Id, 9);

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        SaveDailyAttendance.Response.RaisedAlert raised = Assert.Single(response.Alerts.Raised);
        Assert.Equal(student.Id, raised.StudentId);
        Assert.Equal(10, raised.AbsenceCount);
        Assert.Equal(10, raised.Threshold);

        StudentAlert stored = await SingleAlert(fixture, student.Id);
        Assert.Equal(fixture.SchoolId, stored.SchoolId);
        Assert.Equal(AlertType.ChronicAbsence, stored.AlertType);
        Assert.Equal(AttendanceSubmissionFixture.SchoolYear, stored.SchoolYearStart);
        Assert.Null(stored.ResolvedAt);
        Assert.Null(stored.ResolutionSource);
    }

    [Fact]
    public async Task Handle_WhenTotalIsBelowThreshold_DoesNotRaise()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        await AddAbsencesAsync(fixture, student.Id, 8);

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Equal(9, response.Entries[0].TotalAbsences);
        Assert.Empty(response.Alerts.Raised);
    }

    /// <summary>V-26, through <c>AbsenceRules.ResolveThreshold</c> — the constant lives in one place.</summary>
    [Fact]
    public async Task Handle_WhenSchoolThresholdIsNull_UsesTheDefaultOfTen()
    {
        await using AttendanceSubmissionFixture fixture =
            await AttendanceSubmissionFixture.CreateAsync(absenceAlertThreshold: null);

        Student student = await fixture.AddStudentAsync();

        await AddAbsencesAsync(fixture, student.Id, 9);

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Equal(10, Assert.Single(response.Alerts.Raised).Threshold);
    }

    /// <summary>
    ///     The unique episode index would reject a second one anyway; the handler must not get there.
    /// </summary>
    [Fact]
    public async Task Handle_WhenEpisodeIsAlreadyOpen_DoesNotRaiseASecond()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        await AddAbsencesAsync(fixture, student.Id, 9);
        await fixture.AddAlertAsync(student.Id);

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Empty(response.Alerts.Raised);
        await SingleAlert(fixture, student.Id);
    }

    /// <summary>
    ///     DEC-18: a documented human decision is never silently discarded. Without this, the very next
    ///     save that recounts at or above the threshold opens a fresh episode.
    /// </summary>
    [Fact]
    public async Task Handle_WhenManuallyResolvedThisYearAtThisSchool_DoesNotRaise()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        await AddAbsencesAsync(fixture, student.Id, 12);

        await fixture.AddAlertAsync(
            student.Id,
            resolvedAt: InMemoryDbContextFactory.DefaultNow,
            resolutionSource: ResolutionSource.Manual);

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Equal(13, response.Entries[0].TotalAbsences);
        Assert.Empty(response.Alerts.Raised);
    }

    /// <summary>
    ///     <b>The suppression key includes <c>SchoolId</c>, and DEC-16 records the school-agnostic form
    ///     as a safeguarding failure:</b> a former school's manual resolution suppressed alerting at the
    ///     receiving school for the rest of the year.
    /// </summary>
    [Fact]
    public async Task Handle_WhenManuallyResolvedAtADifferentSchool_StillRaises()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        await AddAbsencesAsync(fixture, student.Id, 9);

        await fixture.AddAlertAsync(
            student.Id,
            schoolId: Guid.NewGuid(),
            resolvedAt: InMemoryDbContextFactory.DefaultNow,
            resolutionSource: ResolutionSource.Manual);

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Single(response.Alerts.Raised);
    }

    /// <summary>
    ///     The other half of the same key. Previously the receiving school could neither raise its own
    ///     alert nor see or resolve the former school's — DEC-15 returns 404 across the boundary.
    /// </summary>
    [Fact]
    public async Task Handle_WhenAnOpenEpisodeExistsAtADifferentSchool_StillRaisesHere()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        await AddAbsencesAsync(fixture, student.Id, 9);
        await fixture.AddAlertAsync(student.Id, schoolId: Guid.NewGuid());

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        SaveDailyAttendance.Response.RaisedAlert raised = Assert.Single(response.Alerts.Raised);

        await using SparkrockRwcDbContext reader = fixture.NewContext();
        StudentAlert here = await reader.StudentAlerts.SingleAsync(alert => alert.Id == raised.AlertId);

        Assert.Equal(fixture.SchoolId, here.SchoolId);
    }

    /// <summary>
    ///     The path DEC-12 calls the quiet one: a correction drops the count and closes an open
    ///     safeguarding episode with no person involved. It is also why the back-dating window exists.
    /// </summary>
    [Fact]
    public async Task Handle_WhenCorrectionDropsTotalBelowThreshold_AutoResolvesTheOpenEpisode()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        await AddAbsencesAsync(fixture, student.Id, 9);
        await fixture.AddAttendanceAsync(student.Id, AttendanceSubmissionFixture.SubmittedDate);
        await fixture.AddAlertAsync(student.Id, absenceCount: 10);

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(
                AttendanceSubmissionFixture.Entry(student.Id, AttendanceSubmissionFixture.PresentCode)),
            CancellationToken.None);

        Assert.Equal(9, response.Entries[0].TotalAbsences);

        SaveDailyAttendance.Response.ResolvedAlert resolved = Assert.Single(response.Alerts.Resolved);
        Assert.Equal(nameof(ResolutionSource.AutoBelowThreshold), resolved.Source);

        StudentAlert stored = await SingleAlert(fixture, student.Id);
        Assert.Equal(fixture.Clock.GetUtcNow(), stored.ResolvedAt);
        Assert.Equal(fixture.CurrentUser.UserId, stored.ResolvedBy);
        Assert.Equal(ResolutionSource.AutoBelowThreshold, stored.ResolutionSource);

        // ck_student_alerts_resolution_consistent needs the source; the reason stays null because the
        // source already says what happened.
        Assert.Null(stored.ResolutionReason);
    }

    /// <summary>Resolve strictly below the threshold — <b>no hysteresis</b> (DEC-18).</summary>
    [Fact]
    public async Task Handle_WhenTotalEqualsThreshold_DoesNotAutoResolve()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        await AddAbsencesAsync(fixture, student.Id, 9);
        await fixture.AddAttendanceAsync(student.Id, AttendanceSubmissionFixture.SubmittedDate);
        await fixture.AddAlertAsync(student.Id, absenceCount: 10);

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Equal(10, response.Entries[0].TotalAbsences);
        Assert.Empty(response.Alerts.Resolved);
        Assert.Empty(response.Alerts.Raised);
    }

    [Fact]
    public async Task Handle_WhenNoEpisodeIsOpen_DoesNotAutoResolve()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(
                AttendanceSubmissionFixture.Entry(student.Id, AttendanceSubmissionFixture.PresentCode)),
            CancellationToken.None);

        Assert.Empty(response.Alerts.Resolved);
    }

    /// <summary>
    ///     The intended reading of DEC-18's <c>is_deleted</c> term, stated so it is a decision rather
    ///     than a discovery: the reflective filter hides a soft-deleted resolution, so it stops
    ///     suppressing.
    /// </summary>
    [Fact]
    public async Task Handle_WhenManuallyResolvedEpisodeIsSoftDeleted_RaisesAgain()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        await AddAbsencesAsync(fixture, student.Id, 9);

        StudentAlert manual = await fixture.AddAlertAsync(
            student.Id,
            resolvedAt: InMemoryDbContextFactory.DefaultNow,
            resolutionSource: ResolutionSource.Manual);

        fixture.DbContext.StudentAlerts.Remove(manual);
        await fixture.DbContext.SaveChangesAsync(CancellationToken.None);

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Single(response.Alerts.Raised);
    }

    /// <summary>
    ///     The predicates are complementary on <c>hasOpenEpisode</c>, and the handler expresses the two
    ///     branches as independent <c>if</c>s so that a future edit breaking the complementarity shows
    ///     up here rather than being hidden by the control flow.
    /// </summary>
    [Fact]
    public async Task Handle_NeverRaisesAndAutoResolvesTheSameStudentInOneSave()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();

        Student raising = await fixture.AddStudentAsync();
        Student resolving = await fixture.AddStudentAsync();
        Student quiet = await fixture.AddStudentAsync();

        await AddAbsencesAsync(fixture, raising.Id, 9);

        await AddAbsencesAsync(fixture, resolving.Id, 9);
        await fixture.AddAttendanceAsync(resolving.Id, AttendanceSubmissionFixture.SubmittedDate);
        await fixture.AddAlertAsync(resolving.Id, absenceCount: 10);

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(
                AttendanceSubmissionFixture.Entry(raising.Id),
                AttendanceSubmissionFixture.Entry(resolving.Id, AttendanceSubmissionFixture.PresentCode),
                AttendanceSubmissionFixture.Entry(quiet.Id, AttendanceSubmissionFixture.PresentCode)),
            CancellationToken.None);

        Assert.Equal([raising.Id], response.Alerts.Raised.Select(alert => alert.StudentId));
        Assert.Equal([resolving.Id], response.Alerts.Resolved.Select(alert => alert.StudentId));

        Assert.Empty(response.Alerts.Raised
            .Select(alert => alert.StudentId)
            .Intersect(response.Alerts.Resolved.Select(alert => alert.StudentId)));
    }

    /// <summary>
    ///     Audit only. Comparisons always use the school's <em>current</em> threshold, so raising a
    ///     threshold does not retroactively re-evaluate anyone (DEC-18) — F10's triage query is what
    ///     lists the alerts a threshold change stranded.
    /// </summary>
    [Fact]
    public async Task Handle_StoresResolvedThresholdInThresholdAtRaise()
    {
        await using AttendanceSubmissionFixture fixture =
            await AttendanceSubmissionFixture.CreateAsync(absenceAlertThreshold: 4);

        Student student = await fixture.AddStudentAsync();

        await AddAbsencesAsync(fixture, student.Id, 3);

        await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Equal(4, (await SingleAlert(fixture, student.Id)).ThresholdAtRaise);
    }

    /// <summary>V-20: a chronically absent student who is not in the payload gets no alert.</summary>
    [Fact]
    public async Task Handle_OnlyEvaluatesSubmittedStudents()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();

        Student submitted = await fixture.AddStudentAsync();
        Student omitted = await fixture.AddStudentAsync();

        await AddAbsencesAsync(fixture, omitted.Id, 12);

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(submitted.Id)), CancellationToken.None);

        Assert.Empty(response.Alerts.Raised);

        await using SparkrockRwcDbContext reader = fixture.NewContext();
        Assert.Empty(await reader.StudentAlerts.ToListAsync());
    }

    // ------------------------------------------------------------------------------- helpers

    /// <summary>
    ///     <paramref name="count" /> absences on consecutive days before the submitted date, all inside
    ///     the same school year.
    /// </summary>
    private static async Task AddAbsencesAsync(AttendanceSubmissionFixture fixture, Guid studentId, int count)
    {
        for (int day = 1; day <= count; day++)
            await fixture.AddAttendanceAsync(studentId, AttendanceSubmissionFixture.SubmittedDate.AddDays(-day));
    }

    private static async Task<StudentAlert> SingleAlert(AttendanceSubmissionFixture fixture, Guid studentId)
    {
        await using SparkrockRwcDbContext reader = fixture.NewContext();

        return await reader.StudentAlerts.SingleAsync(alert => alert.StudentId == studentId);
    }
}
