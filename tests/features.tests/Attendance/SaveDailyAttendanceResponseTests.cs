using System.Text.Json;
using domain.Attendance;
using domain.Students;
using features.Attendance;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;

namespace features.tests.Attendance;

/// <summary>
///     The submission log and the 201 body, field for field against spec §6.
/// </summary>
public sealed class SaveDailyAttendanceResponseTests
{
    [Fact]
    public async Task Handle_WritesOneSubmissionLogRow()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();

        Student first = await fixture.AddStudentAsync();
        Student second = await fixture.AddStudentAsync();

        SaveDailyAttendance.Command command = new()
        {
            SchoolId = fixture.SchoolId,
            Date = "2026-09-14",
            IdempotencyKey = "8f14e45fceea167a5a36dedd4bea2543",
            Entries =
            [
                AttendanceSubmissionFixture.Entry(first.Id),
                AttendanceSubmissionFixture.Entry(second.Id)
            ]
        };

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(command, CancellationToken.None);

        AttendanceSubmissionLog log = await SingleLog(fixture);

        Assert.Equal(fixture.SchoolId, log.SchoolId);
        Assert.Equal(AttendanceSubmissionFixture.SubmittedDate, log.AttendDate);
        Assert.Equal(response.SubmittedAt, log.SubmittedAt);
        Assert.Equal(2, log.RecordCount);
        Assert.Equal(fixture.CurrentUser.UserId, log.SubmittedBy);
        Assert.Equal("8f14e45fceea167a5a36dedd4bea2543", log.IdempotencyKey);
    }

    /// <summary>The <c>Location</c> target (O-01): <c>/api/v1/attendance-submissions/{submissionId}</c>.</summary>
    [Fact]
    public async Task Handle_ResponseSubmissionIdMatchesTheLogRowId()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Equal(response.SubmissionId, (await SingleLog(fixture)).Id);
    }

    [Fact]
    public async Task Handle_ResponseCountsAgree()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();

        Student created = await fixture.AddStudentAsync();
        Student updated = await fixture.AddStudentAsync();

        await fixture.AddAttendanceAsync(updated.Id, AttendanceSubmissionFixture.SubmittedDate);

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(
                AttendanceSubmissionFixture.Entry(created.Id),
                AttendanceSubmissionFixture.Entry(updated.Id)),
            CancellationToken.None);

        Assert.Equal(2, response.RecordCount);
        Assert.Equal(response.RecordCount, response.Entries.Count);
        Assert.Equal(response.RecordCount, response.CreatedCount + response.UpdatedCount);
        Assert.Equal(1, response.CreatedCount);
        Assert.Equal(1, response.UpdatedCount);
    }

    /// <summary>
    ///     Every element carries its own <c>studentId</c> and the contract is that clients match on it.
    ///     A client that reorders its grid between render and submit would otherwise map results to the
    ///     wrong students — and these results include safeguarding-relevant absence totals.
    /// </summary>
    [Fact]
    public async Task Handle_ResponseEntriesCarryStudentIdAndAreNotPositional()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();

        Student withHistory = await fixture.AddStudentAsync();
        Student clean = await fixture.AddStudentAsync();

        await fixture.AddAttendanceAsync(withHistory.Id, AttendanceSubmissionFixture.SubmittedDate.AddDays(-1));
        await fixture.AddAttendanceAsync(withHistory.Id, AttendanceSubmissionFixture.SubmittedDate.AddDays(-2));

        // Submitted second, and its result must still be its own.
        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(
                AttendanceSubmissionFixture.Entry(clean.Id),
                AttendanceSubmissionFixture.Entry(withHistory.Id)),
            CancellationToken.None);

        Assert.Equal(1, response.Entries.Single(entry => entry.StudentId == clean.Id).TotalAbsences);
        Assert.Equal(3, response.Entries.Single(entry => entry.StudentId == withHistory.Id).TotalAbsences);
    }

    /// <summary>D-02 and V-23: echoing is the only way a client sees what was actually recorded.</summary>
    [Fact]
    public async Task Handle_ResponseEchoesTheFourSnapshotFields()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(
                AttendanceSubmissionFixture.Entry(student.Id, AttendanceSubmissionFixture.ExcusedCode)),
            CancellationToken.None);

        SaveDailyAttendance.Response.EntryResult entry = Assert.Single(response.Entries);

        Assert.Equal(AttendanceSubmissionFixture.ExcusedCode, entry.AttendCode);
        Assert.Equal("Absent — excused", entry.AttendCodeDescription);
        Assert.True(entry.IsAbsent);
        Assert.True(entry.IsExcused);
        Assert.Equal(SaveDailyAttendance.OutcomeCreated, entry.Outcome);
    }

    /// <summary>
    ///     O-17 and conventions §2. Asserted over the serialised body rather than field by field,
    ///     because a field added later would slip past a field-by-field check.
    /// </summary>
    [Fact]
    public async Task Handle_ResponseNeverContainsNotes()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        const string Notes = "Child disclosed abuse at home; social worker informed.";

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id, notes: Notes)),
            CancellationToken.None);

        Assert.DoesNotContain(Notes, JsonSerializer.Serialize(response), StringComparison.Ordinal);

        // Stored, though — F06's roster and F08's history are the read paths that return it.
        await using SparkrockRwcDbContext reader = fixture.NewContext();
        Assert.Equal(Notes, (await reader.StudentAttendances.SingleAsync()).Notes);
    }

    /// <summary>
    ///     Design §4's response contract does not carry <c>minutesLate</c>. Adding a field to a
    ///     canonical wire shape is not this feature's to do unilaterally (plan, conflict 9), so the
    ///     value is currently write-only until F06 or F08 reads it back.
    /// </summary>
    [Fact]
    public async Task Handle_ResponseOmitsMinutesLate()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(
                AttendanceSubmissionFixture.Entry(
                    student.Id, AttendanceSubmissionFixture.LateCode, minutesLate: 12)),
            CancellationToken.None);

        Assert.DoesNotContain(
            "minutesLate", JsonSerializer.Serialize(response), StringComparison.OrdinalIgnoreCase);

        await using SparkrockRwcDbContext reader = fixture.NewContext();
        Assert.Equal(12, (await reader.StudentAttendances.SingleAsync()).MinutesLate);
    }

    [Fact]
    public async Task Handle_ResponseCarriesSchoolYearAndLabel()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Equal(2026, response.SchoolYear);
        Assert.Equal("2026-2027", response.SchoolYearLabel);
        Assert.Equal(fixture.SchoolId, response.SchoolId);
        Assert.Equal(AttendanceSubmissionFixture.SubmittedDate, response.AttendanceDate);
        Assert.Equal(fixture.CurrentUser.UserId, response.SubmittedBy.UserId);
        Assert.Equal(fixture.CurrentUser.DisplayName, response.SubmittedBy.DisplayName);
    }

    [Fact]
    public async Task Handle_ResponseAlertsAreEmptyArraysWhenNothingHappened()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.NotNull(response.Alerts.Raised);
        Assert.NotNull(response.Alerts.Resolved);
        Assert.Empty(response.Alerts.Raised);
        Assert.Empty(response.Alerts.Resolved);

        string json = JsonSerializer.Serialize(response);
        Assert.Contains("\"Raised\":[]", json, StringComparison.Ordinal);
        Assert.Contains("\"Resolved\":[]", json, StringComparison.Ordinal);
    }

    /// <summary>
    ///     DEC-16: it is the student's <em>current</em> school, and returning it to a former school
    ///     discloses where a child moved to — precisely the datum that must not flow backwards for a
    ///     transfer driven by care placement or domestic abuse. Only the threshold value appears.
    /// </summary>
    [Fact]
    public async Task Handle_ResponseNeverContainsAThresholdSourceSchoolId()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        for (int day = 1; day <= 9; day++)
            await fixture.AddAttendanceAsync(student.Id, AttendanceSubmissionFixture.SubmittedDate.AddDays(-day));

        SaveDailyAttendance.Response response = await fixture.Handler().Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Single(response.Alerts.Raised);

        string json = JsonSerializer.Serialize(response);

        Assert.DoesNotContain("thresholdSource", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("thresholdSourceSchoolId", json, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<AttendanceSubmissionLog> SingleLog(AttendanceSubmissionFixture fixture)
    {
        await using SparkrockRwcDbContext reader = fixture.NewContext();

        return await reader.AttendanceSubmissionLogs.SingleAsync();
    }
}
