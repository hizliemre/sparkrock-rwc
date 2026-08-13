using System.Data;
using domain.Alerts;
using domain.Attendance;
using domain.Exceptions;
using domain.Students;
using features.Attendance;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace features.integration.tests.Attendance;

/// <summary>
///     The half of F07 that only a real PostgreSQL can verify.
/// </summary>
/// <remarks>
///     <b>VC-35 is why this file exists.</b> EF InMemory builds the <c>uint</c>/<c>xmin</c> concurrency
///     token and never populates it, so the token stays zero and every concurrency check passes
///     trivially; it also enforces no unique index and produces no <c>SqlState</c>. Every handler-tier
///     assertion about a race, a retry, a lost update or atomicity therefore passes whether or not the
///     mechanism exists — it would pass with DEC-14's retry loop deleted. Nothing in this file would.
///     <para>
///         Each race is produced by a second writer on a second connection, committed in the instant
///         between the handler's reads and its single <c>SaveChangesAsync</c>
///         (see <see cref="RacingDbContext" />). No exception is injected anywhere in this file.
///     </para>
/// </remarks>
[Collection(IntegrationTestCollectionDefinition.Name)]
public sealed class SaveDailyAttendanceIntegrationTests(PostgresContainerFixture fixture)
{
    /// <summary>
    ///     <b>Race 1 — the summary token.</b> Two writers hold the same summary; the other commits
    ///     first, so this one's <c>UPDATE … WHERE xmin = @original</c> affects zero rows.
    /// </summary>
    /// <remarks>
    ///     The recovery under test is <c>ReloadAsync</c>. Without it, identity resolution hands attempt
    ///     2 the tracked instance and discards the database values, the stale token is never refreshed,
    ///     and all three attempts fail identically writing nothing (VC-29). The final total is asserted
    ///     to reflect <em>both</em> writers, which is the only way to tell a working reload from a
    ///     handler that simply overwrote the other writer's work.
    /// </remarks>
    [Fact]
    public async Task Handle_WhenSummaryTokenIsStale_ReloadsAndSavesOnAttemptTwo()
    {
        await using AttendanceSaveHarness harness = await AttendanceSaveHarness.CreateAsync(fixture);

        Student student = await harness.AddStudentAsync();

        await harness.AddAttendanceAsync(student.Id, AttendanceSaveHarness.SubmittedDate.AddDays(-5));
        await harness.AddSummaryAsync(student.Id, totalAbsences: 1);

        bool raced = false;

        RacingDbContext racing = new(harness.DbContext)
        {
            BeforeSave = _ =>
            {
                if (raced)
                    return;

                raced = true;

                // Another writer records a different day's absence and updates the summary. This
                // commits on its own connection, so the handler's original xmin is genuinely stale.
                using SparkrockRwcDbContext racer = harness.NewContext();

                racer.StudentAttendances.Add(
                    harness.NewAttendance(student.Id, AttendanceSaveHarness.SubmittedDate.AddDays(-3)));

                StudentAttendanceSummary summary = racer.StudentAttendanceSummaries
                    .Single(row => row.StudentId == student.Id);

                summary.TotalAbsences = 2;

                racer.SaveChangesAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
        };

        SaveDailyAttendance.Response response = await harness.Handler(racing).Handle(
            harness.Command([harness.Entry(student.Id)]), CancellationToken.None);

        Assert.Equal(2, racing.SaveChangesCalls);

        // The recount on attempt 2 sees the racer's row as well as the seeded one, plus today's.
        Assert.Equal(3, response.Entries[0].TotalAbsences);

        await using SparkrockRwcDbContext reader = harness.NewContext();

        Assert.Equal(
            3,
            (await reader.StudentAttendanceSummaries.SingleAsync(row => row.StudentId == student.Id)).TotalAbsences);

        Assert.Equal(3, await reader.StudentAttendances.CountAsync(row => row.StudentId == student.Id));
    }

    /// <summary>
    ///     <b>Race 2 — the summary first-insert</b>, <c>ix_summaries_student_id_school_year_start</c>
    ///     (VC-03: <c>FOR UPDATE</c> locks nothing that does not yet exist, so any locking strategy
    ///     still needs this path).
    /// </summary>
    [Fact]
    public async Task Handle_WhenSummaryIsFirstInsertedByARacer_DetachesAndUpdatesOnAttemptTwo()
    {
        await using AttendanceSaveHarness harness = await AttendanceSaveHarness.CreateAsync(fixture);

        Student student = await harness.AddStudentAsync();

        bool raced = false;

        RacingDbContext racing = new(harness.DbContext)
        {
            BeforeSave = _ =>
            {
                if (raced)
                    return;

                raced = true;

                using SparkrockRwcDbContext racer = harness.NewContext();

                racer.StudentAttendanceSummaries.Add(new StudentAttendanceSummary
                {
                    Id = Guid.NewGuid(),
                    StudentId = student.Id,
                    SchoolId = harness.SchoolId,
                    SchoolYearStart = AttendanceSaveHarness.SchoolYear,
                    TotalAbsences = 0
                });

                racer.SaveChangesAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
        };

        SaveDailyAttendance.Response response = await harness.Handler(racing).Handle(
            harness.Command([harness.Entry(student.Id)]), CancellationToken.None);

        Assert.Equal(2, racing.SaveChangesCalls);
        Assert.Equal(1, response.Entries[0].TotalAbsences);

        await using SparkrockRwcDbContext reader = harness.NewContext();

        // One row, not two: attempt 2 took the update branch on the racer's row.
        StudentAttendanceSummary summary = await reader.StudentAttendanceSummaries
            .SingleAsync(row => row.StudentId == student.Id);

        Assert.Equal(1, summary.TotalAbsences);
    }

    /// <summary>
    ///     <b>Race 3 — the attendance first-insert, and the regression this feature exists to
    ///     prevent.</b>
    /// </summary>
    /// <remarks>
    ///     DEC-14: <c>ix_student_attendances_student_id_attend_date</c> was previously mapped straight
    ///     to a 409, "which would have failed a whole 28-student batch on one racing student". Twenty
    ///     eight students, one of whom another school records first; the assertion is twenty-eight rows
    ///     and a response, not a conflict.
    /// </remarks>
    [Fact]
    public async Task Handle_WhenAnotherSchoolInsertsAttendanceFirst_SavesTheWholeBatchOnAttemptTwo()
    {
        await using AttendanceSaveHarness harness = await AttendanceSaveHarness.CreateAsync(fixture);

        List<Student> students = [];

        for (int index = 0; index < 28; index++)
            students.Add(await harness.AddStudentAsync());

        domain.Schools.School otherSchool = await harness.AddSchoolAsync();

        Student racedStudent = students[13];

        bool raced = false;
        int entriesOnFirstFailure = -1;

        RacingDbContext racing = new(harness.DbContext)
        {
            BeforeSave = _ =>
            {
                if (raced)
                    return;

                raced = true;

                using SparkrockRwcDbContext racer = harness.NewContext();

                racer.StudentAttendances.Add(harness.NewAttendance(
                    racedStudent.Id, AttendanceSaveHarness.SubmittedDate, schoolId: otherSchool.Id));

                racer.SaveChangesAsync(CancellationToken.None).GetAwaiter().GetResult();
            },

            // Plan R-2: VC-29 pins DbUpdateException.Entries for a three-entity concurrency batch and
            // for the summary first-insert 23505, never for this case. Measured here rather than
            // relied upon — the handler's recovery keeps its own list precisely because this number is
            // not guaranteed to cover the batch.
            OnSaveFailed = (_, exception) =>
            {
                if (entriesOnFirstFailure < 0 && exception is ConcurrencyConflictException conflict)
                    entriesOnFirstFailure = conflict.Entries.Count;
            }
        };

        SaveDailyAttendance.Response response = await harness.Handler(racing).Handle(
            harness.Command([.. students.Select(student => harness.Entry(student.Id))]),
            CancellationToken.None);

        Assert.Equal(2, racing.SaveChangesCalls);
        Assert.Equal(28, response.RecordCount);
        Assert.Equal(28, response.CreatedCount + response.UpdatedCount);

        // The racing student's row was updated rather than inserted; the other twenty-seven were new.
        Assert.Equal(1, response.UpdatedCount);
        Assert.Equal(27, response.CreatedCount);

        await using SparkrockRwcDbContext reader = harness.NewContext();

        Guid[] studentIds = [.. students.Select(student => student.Id)];

        Assert.Equal(
            28,
            await reader.StudentAttendances.CountAsync(
                row => studentIds.Contains(row.StudentId) && row.AttendDate == AttendanceSaveHarness.SubmittedDate));

        // The measurement, recorded in the assertion message so a run reports it even when green.
        Assert.True(
            entriesOnFirstFailure >= 1,
            $"DbUpdateException.Entries carried {entriesOnFirstFailure} entries for a 28-row batch "
            + "whose attendance insert violated the unique index.");
    }

    /// <summary>
    ///     Attempt exhaustion. A repeatable race — another writer bumps the summary before every save
    ///     — so all three attempts lose and the caller gets one 409 rather than an unbounded retry.
    /// </summary>
    [Fact]
    public async Task Handle_WhenTheRaceRepeats_ReturnsConflictAfterThreeAttempts()
    {
        await using AttendanceSaveHarness harness = await AttendanceSaveHarness.CreateAsync(fixture);

        Student student = await harness.AddStudentAsync();
        await harness.AddSummaryAsync(student.Id, totalAbsences: 0);

        RacingDbContext racing = new(harness.DbContext)
        {
            BeforeSave = save =>
            {
                using SparkrockRwcDbContext racer = harness.NewContext();

                StudentAttendanceSummary summary = racer.StudentAttendanceSummaries
                    .Single(row => row.StudentId == student.Id);

                // A different value every attempt, so the row's xmin really moves each time.
                summary.TotalAbsences = 50 + save;

                racer.SaveChangesAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
        };

        ConflictException exception = await Assert.ThrowsAsync<ConflictException>(() =>
            harness.Handler(racing).Handle(
                harness.Command([harness.Entry(student.Id)]), CancellationToken.None));

        Assert.Equal(ErrorCodes.Attendance.ConcurrentSubmission, exception.ErrorCode);
        Assert.Equal(AttendanceSave.MaxAttempts, racing.SaveChangesCalls);

        // Nothing of the submission survived.
        await using SparkrockRwcDbContext reader = harness.NewContext();

        Assert.Equal(0, await reader.StudentAttendances.CountAsync(row => row.StudentId == student.Id));
        Assert.Equal(0, await reader.AttendanceSubmissionLogs.CountAsync(row => row.SchoolId == harness.SchoolId));
    }

    /// <summary>
    ///     <b>A foreign-key violation is permanent and is not retried.</b> DEC-14: matching on
    ///     <c>DbUpdateException</c> alone would burn the whole attempt bound before reporting a
    ///     reference that will never resolve.
    /// </summary>
    /// <remarks>
    ///     The student is deleted between stage C's roster check and the insert — the TOCTOU window
    ///     design §4 accepts, provoked deliberately. It is deleted with a plain <c>DELETE</c> on a
    ///     separate connection because <c>Student</c> derives from <c>BaseEntity</c>, so
    ///     <c>Remove()</c> is refused by the audit interceptor (DEC-20).
    /// </remarks>
    [Fact]
    public async Task Handle_WhenAForeignKeyIsViolated_DoesNotRetry()
    {
        await using AttendanceSaveHarness harness = await AttendanceSaveHarness.CreateAsync(fixture);

        Student student = await harness.AddStudentAsync();

        RacingDbContext racing = new(harness.DbContext)
        {
            BeforeSave = _ => DeleteStudent(harness.ConnectionString, student.Id)
        };

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            harness.Handler(racing).Handle(
                harness.Command([harness.Entry(student.Id)]), CancellationToken.None));

        Assert.Equal(1, racing.SaveChangesCalls);
        Assert.Equal("23503", ((PostgresException)exception.InnerException!).SqlState);
    }

    /// <summary>
    ///     A check violation is permanent too. <c>ck_student_attendances_minutes_late_not_negative</c>
    ///     is the backstop behind the validator rule; the handler is driven directly here, so the
    ///     validator never runs and the constraint is what refuses the row.
    /// </summary>
    [Fact]
    public async Task Handle_WhenACheckConstraintIsViolated_DoesNotRetry()
    {
        await using AttendanceSaveHarness harness = await AttendanceSaveHarness.CreateAsync(fixture);

        Student student = await harness.AddStudentAsync();

        RacingDbContext racing = new(harness.DbContext);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            harness.Handler(racing).Handle(
                harness.Command([harness.Entry(student.Id, minutesLate: -5)]), CancellationToken.None));

        Assert.Equal(1, racing.SaveChangesCalls);
        Assert.Equal("23514", ((PostgresException)exception.InnerException!).SqlState);
    }

    /// <summary>
    ///     <b>V-03's <c>Verified by</c>.</b> One <c>SaveChangesAsync</c> is one implicit transaction
    ///     (VC-32): a <c>23505</c> on the attendance insert rolls back the summary, the alert and the
    ///     log with it. Legacy had no transaction at all (L-03), and the procedure could not even be
    ///     created (L-13).
    /// </summary>
    /// <remarks>
    ///     Asserted from a <em>separate connection</em>, at the instant the attempt fails, because that
    ///     is the only moment at which a partial write would be observable — the retry then succeeds
    ///     and covers its own tracks.
    /// </remarks>
    [Fact]
    public async Task SaveChangesAsync_WhenAttendanceInsertViolatesUniqueIndex_RollsBackSummaryAlertAndLog()
    {
        await using AttendanceSaveHarness harness = await AttendanceSaveHarness.CreateAsync(fixture);

        Student student = await harness.AddStudentAsync();
        domain.Schools.School otherSchool = await harness.AddSchoolAsync();

        // Nine prior absences, so this submission would also raise an alert — the batch therefore
        // carries an attendance row, a summary, an alert and the log.
        for (int day = 1; day <= 9; day++)
            await harness.AddAttendanceAsync(student.Id, AttendanceSaveHarness.SubmittedDate.AddDays(-day));

        bool raced = false;
        long summariesAfterRollback = -1;
        long alertsAfterRollback = -1;
        long logsAfterRollback = -1;

        RacingDbContext racing = new(harness.DbContext)
        {
            BeforeSave = _ =>
            {
                if (raced)
                    return;

                raced = true;

                using SparkrockRwcDbContext racer = harness.NewContext();

                racer.StudentAttendances.Add(harness.NewAttendance(
                    student.Id, AttendanceSaveHarness.SubmittedDate, schoolId: otherSchool.Id));

                racer.SaveChangesAsync(CancellationToken.None).GetAwaiter().GetResult();
            },

            OnSaveFailed = (save, _) =>
            {
                if (save != 1)
                    return;

                summariesAfterRollback = CountAsync(
                    harness.ConnectionString,
                    "SELECT count(*) FROM student_attendance_summaries WHERE student_id = @id",
                    student.Id);

                alertsAfterRollback = CountAsync(
                    harness.ConnectionString,
                    "SELECT count(*) FROM student_alerts WHERE student_id = @id",
                    student.Id);

                logsAfterRollback = CountAsync(
                    harness.ConnectionString,
                    "SELECT count(*) FROM attendance_submission_logs WHERE school_id = @id",
                    harness.SchoolId);
            }
        };

        SaveDailyAttendance.Response response = await harness.Handler(racing).Handle(
            harness.Command([harness.Entry(student.Id)]), CancellationToken.None);

        Assert.Equal(0, summariesAfterRollback);
        Assert.Equal(0, alertsAfterRollback);
        Assert.Equal(0, logsAfterRollback);

        // And the retry then wrote all four.
        Assert.Equal(2, racing.SaveChangesCalls);
        Assert.Single(response.Alerts.Raised);

        await using SparkrockRwcDbContext reader = harness.NewContext();

        Assert.Equal(1, await reader.StudentAttendanceSummaries.CountAsync(row => row.StudentId == student.Id));
        Assert.Equal(1, await reader.StudentAlerts.CountAsync(row => row.StudentId == student.Id));
        Assert.Equal(1, await reader.AttendanceSubmissionLogs.CountAsync(row => row.SchoolId == harness.SchoolId));
    }

    /// <summary>
    ///     <b>V-06 ●'s <c>Verified by</c>.</b> The dedup key is <c>(StudentId, AttendDate)</c>
    ///     <em>globally</em>, exactly as legacy had it. L-05's school disagreement is resolved by
    ///     validating membership (DEC-08), not by widening the key — widening it would let a
    ///     transferred student have two rows for one day.
    /// </summary>
    [Fact]
    public async Task SaveChanges_WhenTwoSchoolsSubmitTheSameStudentAndDate_ViolatesTheStudentDateUniqueIndex()
    {
        await using AttendanceSaveHarness harness = await AttendanceSaveHarness.CreateAsync(fixture);

        Student student = await harness.AddStudentAsync();
        domain.Schools.School otherSchool = await harness.AddSchoolAsync();

        await harness.AddAttendanceAsync(student.Id, AttendanceSaveHarness.SubmittedDate);

        await using SparkrockRwcDbContext second = harness.NewContext();

        second.StudentAttendances.Add(harness.NewAttendance(
            student.Id, AttendanceSaveHarness.SubmittedDate, schoolId: otherSchool.Id));

        ConcurrencyConflictException exception = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => second.SaveChangesAsync(CancellationToken.None));

        Assert.Equal("ix_student_attendances_student_id_attend_date", exception.ConstraintName);
        Assert.Equal(ErrorCodes.Attendance.ConcurrentSubmission, exception.ErrorCode);
    }

    /// <summary>
    ///     The index filter <c>WHERE is_deleted = false</c> is present and effective, so a withdrawn
    ///     correction frees the slot for its replacement.
    /// </summary>
    [Fact]
    public async Task SaveChanges_WhenARowIsSoftDeleted_AllowsANewRowForTheSameStudentAndDate()
    {
        await using AttendanceSaveHarness harness = await AttendanceSaveHarness.CreateAsync(fixture);

        Student student = await harness.AddStudentAsync();

        StudentAttendance first = await harness.AddAttendanceAsync(student.Id, AttendanceSaveHarness.SubmittedDate);

        harness.DbContext.StudentAttendances.Remove(first);
        await harness.DbContext.SaveChangesAsync(CancellationToken.None);

        await using SparkrockRwcDbContext second = harness.NewContext();

        second.StudentAttendances.Add(harness.NewAttendance(student.Id, AttendanceSaveHarness.SubmittedDate));

        await second.SaveChangesAsync(CancellationToken.None);

        // One visible, two physically — the filter hides the withdrawn one rather than losing it.
        Assert.Equal(
            1,
            await second.StudentAttendances.CountAsync(
                row => row.StudentId == student.Id && row.AttendDate == AttendanceSaveHarness.SubmittedDate));

        Assert.Equal(
            2,
            CountAsync(
                harness.ConnectionString,
                "SELECT count(*) FROM student_attendances WHERE student_id = @id",
                student.Id));
    }

    /// <summary>
    ///     <b>O-09.</b> A replay within the same school is a 409 <c>ATTENDANCE.DUPLICATE_SUBMISSION</c>,
    ///     and it must <b>not</b> be retried — the key is client-supplied, so every attempt collides
    ///     identically and a retry would burn DEC-14's whole bound to return the same error.
    /// </summary>
    [Fact]
    public async Task Handle_WhenIdempotencyKeyIsReplayed_ReturnsConflict()
    {
        await using AttendanceSaveHarness harness = await AttendanceSaveHarness.CreateAsync(fixture);

        Student student = await harness.AddStudentAsync();

        string key = Guid.NewGuid().ToString("N");

        await harness.Handler().Handle(
            harness.Command([harness.Entry(student.Id)], idempotencyKey: key), CancellationToken.None);

        await using SparkrockRwcDbContext second = harness.NewContext();

        RacingDbContext racing = new(second);

        ConflictException exception = await Assert.ThrowsAsync<ConflictException>(() =>
            harness.Handler(racing).Handle(
                harness.Command([harness.Entry(student.Id)], idempotencyKey: key), CancellationToken.None));

        Assert.Equal(ErrorCodes.Attendance.DuplicateSubmission, exception.ErrorCode);

        // One attempt. A retryable classification here is the defect this asserts against.
        Assert.Equal(1, racing.SaveChangesCalls);

        // And it is not a ConcurrencyConflictException, which the retry predicate would have caught.
        Assert.IsNotType<ConcurrencyConflictException>(exception);
    }

    /// <summary>
    ///     The index is scoped to <c>school_id</c> (F01d §4.3), so one school's retry key is never
    ///     another school's conflict.
    /// </summary>
    [Fact]
    public async Task Handle_WhenIdempotencyKeyIsReusedByAnotherSchool_Succeeds()
    {
        await using AttendanceSaveHarness first = await AttendanceSaveHarness.CreateAsync(fixture);
        await using AttendanceSaveHarness second = await AttendanceSaveHarness.CreateAsync(fixture);

        Student firstStudent = await first.AddStudentAsync();
        Student secondStudent = await second.AddStudentAsync();

        string key = Guid.NewGuid().ToString("N");

        await first.Handler().Handle(
            first.Command([first.Entry(firstStudent.Id)], idempotencyKey: key), CancellationToken.None);

        SaveDailyAttendance.Response response = await second.Handler().Handle(
            second.Command([second.Entry(secondStudent.Id)], idempotencyKey: key), CancellationToken.None);

        Assert.Equal(1, response.CreatedCount);
    }

    /// <summary>The filtered index permits many nulls, so an absent key means no uniqueness at all.</summary>
    [Fact]
    public async Task Handle_WhenIdempotencyKeyIsAbsent_AllowsRepeatedSubmissions()
    {
        await using AttendanceSaveHarness harness = await AttendanceSaveHarness.CreateAsync(fixture);

        Student student = await harness.AddStudentAsync();

        await harness.Handler().Handle(harness.Command([harness.Entry(student.Id)]), CancellationToken.None);

        SaveDailyAttendance.Response second = await harness.Handler().Handle(
            harness.Command([harness.Entry(student.Id, absent: false)]), CancellationToken.None);

        // The second is an update of the same row, and it produced a second log entry — which is
        // exactly why conventions §1 makes this POST rather than PUT.
        Assert.Equal(1, second.UpdatedCount);

        await using SparkrockRwcDbContext reader = harness.NewContext();

        Assert.Equal(2, await reader.AttendanceSubmissionLogs.CountAsync(row => row.SchoolId == harness.SchoolId));
    }

    /// <summary>
    ///     <b>V-07a's <c>Verified by</c>.</b> One grouped recount for the whole batch, never one per
    ///     student — legacy re-aggregated on every cursor iteration (L-08).
    /// </summary>
    /// <remarks>
    ///     Counted with a <see cref="DbCommandInterceptor" /> over a twenty-eight student batch,
    ///     because the shape of the returned dictionary cannot distinguish one grouped query from
    ///     twenty-eight scalar ones. This replaces the divergence log's prose <c>Verified by</c>, which
    ///     O-33 flags as failing the cross-reference check.
    /// </remarks>
    [Fact]
    public async Task Handle_IssuesExactlyOneRecountQueryForTheWholeBatch()
    {
        await using AttendanceSaveHarness harness = await AttendanceSaveHarness.CreateAsync(fixture);

        List<Student> students = [];

        for (int index = 0; index < 28; index++)
            students.Add(await harness.AddStudentAsync());

        RecordingCommandInterceptor recorder = new();

        await using SparkrockRwcDbContext recorded = harness.NewContext([recorder]);

        recorder.Enabled = true;

        await harness.Handler(recorded).Handle(
            harness.Command([.. students.Select(student => harness.Entry(student.Id))]),
            CancellationToken.None);

        recorder.Enabled = false;

        string[] recounts = recorder.Commands
            .Where(text => text.Contains("count(*)", StringComparison.OrdinalIgnoreCase)
                           && text.Contains("student_attendances", StringComparison.Ordinal)
                           && text.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Single(recounts);

        // And the whole batch is one INSERT round trip family, not one save per entry.
        Assert.Equal(1, recorder.SaveCommands);
    }

    /// <summary>
    ///     The largest submission the contract accepts, in one <c>SaveChangesAsync</c> — no hidden
    ///     per-entry save.
    /// </summary>
    [Fact]
    public async Task Handle_WhenBatchIsFiveHundred_CompletesInOneSaveChanges()
    {
        await using AttendanceSaveHarness harness = await AttendanceSaveHarness.CreateAsync(fixture);

        List<Student> students = [];

        for (int index = 0; index < AttendanceSave.MaxBatchSize; index++)
        {
            students.Add(new Student
            {
                Id = Guid.NewGuid(),
                SchoolId = harness.SchoolId,
                FirstName = "Batch",
                LastName = "Student",
                Grade = "09",
                IsActive = true
            });
        }

        harness.DbContext.Students.AddRange(students);
        await harness.DbContext.SaveChangesAsync(CancellationToken.None);

        RacingDbContext racing = new(harness.DbContext);

        SaveDailyAttendance.Response response = await harness.Handler(racing).Handle(
            harness.Command([.. students.Select(student => harness.Entry(student.Id))]),
            CancellationToken.None);

        Assert.Equal(1, racing.SaveChangesCalls);
        Assert.Equal(AttendanceSave.MaxBatchSize, response.CreatedCount);
    }

    /// <summary>
    ///     The model-level assertion behind the whole retry design: the token is <c>uint</c> mapped to
    ///     the <c>xmin</c> system column, not a <c>byte[]</c> mapped to a real column nothing populates
    ///     (VC-28, VC-37).
    /// </summary>
    [Fact]
    public async Task Model_SummaryConcurrencyTokenMapsToXmin()
    {
        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();

        IProperty token = dbContext.Model
            .FindEntityType(typeof(StudentAttendanceSummary))!
            .GetProperties()
            .Single(property => property.IsConcurrencyToken);

        Assert.Equal(typeof(uint), token.ClrType);
        Assert.Equal("xmin", token.GetColumnName());
        Assert.Equal("xid", token.GetColumnType());

        // And no bytea column crept in beside it.
        Assert.DoesNotContain(
            await DatabaseProbe.StringsAsync(
                fixture.ConnectionString,
                "SELECT data_type FROM information_schema.columns WHERE table_name = 'student_attendance_summaries'"),
            type => string.Equals(type, "bytea", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------------------- helpers

    private static long CountAsync(string connectionString, string sql, Guid id)
    {
        using NpgsqlConnection connection = new(connectionString);
        connection.Open();

        using NpgsqlCommand command = new(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("id", DbType.Guid) { Value = id });

        return (long)command.ExecuteScalar()!;
    }

    private static void DeleteStudent(string connectionString, Guid studentId)
    {
        using NpgsqlConnection connection = new(connectionString);
        connection.Open();

        using NpgsqlCommand command = new("DELETE FROM students WHERE id = @id", connection);
        command.Parameters.Add(new NpgsqlParameter("id", DbType.Guid) { Value = studentId });

        command.ExecuteNonQuery();
    }

    /// <summary>
    ///     Records the SQL the handler issues, so "one grouped recount" is a count rather than a hope.
    /// </summary>
    private sealed class RecordingCommandInterceptor : DbCommandInterceptor
    {
        private readonly List<string> _commands = [];

        public bool Enabled { get; set; }

        public IReadOnlyList<string> Commands => _commands;

        public int SaveCommands { get; private set; }

        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled)
            {
                _commands.Add(command.CommandText);

                if (command.CommandText.Contains("INSERT INTO student_attendances", StringComparison.Ordinal))
                    SaveCommands++;
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled)
            {
                _commands.Add(command.CommandText);

                if (command.CommandText.Contains("INSERT INTO student_attendances", StringComparison.Ordinal))
                    SaveCommands++;
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
