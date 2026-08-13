using System.Reflection;
using domain.Attendance;
using domain.Exceptions;
using domain.Students;
using features.Attendance;
using infra.persistence.postgre;
using infra.persistence.postgre.ErrorTranslation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace features.tests.Attendance;

/// <summary>
///     What the handler tier can honestly say about DEC-14's retry loop, and nothing more.
/// </summary>
/// <remarks>
///     <b>Read VC-35 before adding anything here.</b> EF InMemory builds the <c>uint</c>/<c>xmin</c>
///     concurrency token and never populates it: the token stays zero, original always equals current,
///     and every concurrency check passes trivially. So a handler-tier test asserting that a race is
///     detected, that a lost update is prevented, or that a token mismatch is recovered from
///     <em>passes whether or not the mechanism exists</em> — it would pass with the retry loop
///     deleted.
///     <para>
///         What is asserted here is therefore deliberately narrow: the <em>shape</em> of the loop
///         (exactly one save when nothing fails), which exceptions it declines to retry, that a
///         discarded attempt does not soft-delete what it added, that the submission identity does not
///         drift across attempts, and that the constraint names it matches on are the registry's. Every
///         failure is hand-injected through <see cref="CountingDbContext" /> — a thrown exception, not
///         a real conflict.
///     </para>
///     <para>
///         The three races, attempt exhaustion under a repeatable race, atomicity, the global unique
///         index and idempotency are in <c>features.integration.tests</c> against a real PostgreSQL,
///         and they are the only place any of it is actually verified.
///     </para>
/// </remarks>
public sealed class SaveDailyAttendanceRetryTests
{
    private const string AttendanceDateIndex = "ix_student_attendances_student_id_attend_date";

    private const string SummaryIndex = "ix_summaries_student_id_school_year_start";

    private const string AlertEpisodeIndex = "ix_student_alerts_open_episode";

    /// <summary>
    ///     <b>The guard against VC-36 silence.</b>
    /// </summary>
    /// <remarks>
    ///     <c>features</c> cannot reference <c>infra.persistence.postgre</c>, so the handler's retry
    ///     predicate carries the constraint names as literals. A name that drifts from the registry's
    ///     is not an error anywhere: the registry lookup is ordinal, a miss returns null, the provider
    ///     exception is rethrown raw, and the retry simply never fires. This test project can see both
    ///     sides, so it is the only place the two can be compared — and this codebase has already
    ///     shipped a registry key naming a renamed index, which matched nothing and failed silently.
    /// </remarks>
    [Fact]
    public void RetryableConstraints_MatchTheRegistrysRetryableRows()
    {
        string[] registryRetryable = SchemaConstraintErrors.Mappings
            .Where(mapping => mapping.Value.Retryable)
            .Select(mapping => mapping.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] handlerRetryable = SaveDailyAttendance.RetryableConstraints
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(registryRetryable, handlerRetryable);
    }

    /// <summary>A guard on the guard: three names, not an empty set agreeing with an empty set.</summary>
    [Fact]
    public void RetryableConstraints_AreTheThreeDocumentedRows()
    {
        Assert.Equal(
            [AlertEpisodeIndex, AttendanceDateIndex, SummaryIndex],
            SaveDailyAttendance.RetryableConstraints.Order(StringComparer.Ordinal));
    }

    /// <summary>
    ///     The idempotency-key index must never be retryable: the key is client-supplied, so every
    ///     attempt collides identically and a retry burns the whole bound to return the same error.
    /// </summary>
    [Fact]
    public void RetryableConstraints_ExcludeTheIdempotencyKeyIndex()
    {
        Assert.DoesNotContain(
            SchemaConstraintErrors.Names.SubmissionIdempotencyKey, SaveDailyAttendance.RetryableConstraints);
    }

    [Fact]
    public async Task Handle_WhenNothingRaces_SavesOnTheFirstAttempt()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        CountingDbContext counting = new(fixture.DbContext);

        await fixture.Handler(counting).Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Equal(1, counting.SaveChangesCalls);
    }

    /// <summary>A batch of five hundred is still one save — no hidden per-entry write.</summary>
    [Fact]
    public async Task Handle_WhenBatchIsLarge_StillCallsSaveChangesOnce()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();

        List<SaveDailyAttendance.Entry> entries = [];

        for (int index = 0; index < 50; index++)
        {
            Student student = await fixture.AddStudentAsync();
            entries.Add(AttendanceSubmissionFixture.Entry(student.Id));
        }

        CountingDbContext counting = new(fixture.DbContext);

        SaveDailyAttendance.Response response = await fixture.Handler(counting).Handle(
            fixture.Command([.. entries]), CancellationToken.None);

        Assert.Equal(1, counting.SaveChangesCalls);
        Assert.Equal(50, response.CreatedCount);
    }

    /// <summary>Thrown before the loop, so it can never be retried.</summary>
    [Fact]
    public async Task Handle_WhenBusinessRuleExceptionIsThrown_DoesNotRetry()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();

        CountingDbContext counting = new(fixture.DbContext);

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            fixture.Handler(counting).Handle(
                fixture.Command(AttendanceSubmissionFixture.Entry(Guid.NewGuid())), CancellationToken.None));

        Assert.Equal(0, counting.SaveChangesCalls);
    }

    /// <summary>
    ///     A <see cref="ConflictException" /> — which is what the idempotency-key <c>23505</c> arrives
    ///     as — is a duplicate the caller supplied and will collide identically forever.
    /// </summary>
    [Fact]
    public async Task Handle_WhenAConflictExceptionIsThrown_DoesNotRetry()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        CountingDbContext counting = new(fixture.DbContext)
        {
            FailOn = _ => new ConflictException(
                ErrorCodes.Attendance.DuplicateSubmission, "This submission has already been received.")
        };

        ConflictException exception = await Assert.ThrowsAsync<ConflictException>(() =>
            fixture.Handler(counting).Handle(
                fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None));

        Assert.Equal(ErrorCodes.Attendance.DuplicateSubmission, exception.ErrorCode);
        Assert.Equal(1, counting.SaveChangesCalls);
    }

    /// <summary>
    ///     A permanent violation is not retried. DEC-14: matching on <see cref="DbUpdateException" />
    ///     alone would burn the whole bound before reporting a foreign key that will never resolve.
    /// </summary>
    [Fact]
    public async Task Handle_WhenAnUnmappedDbUpdateExceptionIsThrown_DoesNotRetry()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        CountingDbContext counting = new(fixture.DbContext)
        {
            FailOn = _ => new DbUpdateException("foreign key violation", (Exception?)null)
        };

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            fixture.Handler(counting).Handle(
                fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None));

        Assert.Equal(1, counting.SaveChangesCalls);
    }

    /// <summary>
    ///     A <see cref="ConcurrencyConflictException" /> naming a constraint that is <em>not</em> in the
    ///     retryable set is rethrown rather than retried.
    /// </summary>
    [Fact]
    public async Task Handle_WhenAConcurrencyConflictNamesAnUnlistedConstraint_DoesNotRetry()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        CountingDbContext counting = new(fixture.DbContext)
        {
            FailOn = _ => CountingDbContext.RetryableConflict("ix_something_else_entirely")
        };

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            fixture.Handler(counting).Handle(
                fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None));

        Assert.Equal(1, counting.SaveChangesCalls);
    }

    /// <summary>
    ///     <b>Plan R-3's guard.</b>
    /// </summary>
    /// <remarks>
    ///     Recovery detaches an <c>Added</c> row by calling <c>Remove()</c> on it, which is standard EF
    ///     behaviour and reads like a mistake — a reviewer will "correct" it. If it ever marked the row
    ///     <c>Deleted</c> instead, the audit interceptor's DEC-20 rewrite would turn it into a
    ///     soft-delete UPDATE of a row that does not exist, and the next attempt would fail for a reason
    ///     that has nothing to do with the race.
    ///     <para>
    ///         The injected exception carries an <b>empty</b> <c>Entries</c> list on purpose, so the
    ///         only recovery path exercised is the handler's own list of what it added — the half plan
    ///         R-2 records as load-bearing because VC-29 never pinned <c>Entries</c> for this case.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task Handle_WhenAttemptIsDiscarded_DoesNotSoftDeleteTheDiscardedRows()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();

        Student first = await fixture.AddStudentAsync();
        Student second = await fixture.AddStudentAsync();

        CountingDbContext counting = new(fixture.DbContext)
        {
            FailOn = save => save == 1 ? CountingDbContext.RetryableConflict(AttendanceDateIndex) : null
        };

        SaveDailyAttendance.Response response = await fixture.Handler(counting).Handle(
            fixture.Command(
                AttendanceSubmissionFixture.Entry(first.Id),
                AttendanceSubmissionFixture.Entry(second.Id)),
            CancellationToken.None);

        Assert.Equal(2, counting.SaveChangesCalls);
        Assert.Equal(2, response.CreatedCount);

        await using SparkrockRwcDbContext reader = fixture.NewContext();

        List<StudentAttendance> rows = await reader.StudentAttendances.ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.False(row.IsDeleted));
        Assert.All(rows, row => Assert.Null(row.DeletedAt));

        // The summary and the alert lists take the same route, so assert they came through too.
        Assert.Equal(2, (await reader.StudentAttendanceSummaries.ToListAsync()).Count);
        Assert.Single(await reader.AttendanceSubmissionLogs.ToListAsync());
    }

    /// <summary>
    ///     The submission's identity is created once, before the loop, and must not move: the
    ///     <c>Location</c> header would otherwise point somewhere else on a retry, and the recorded time
    ///     would drift by the retry duration.
    /// </summary>
    [Fact]
    public async Task Handle_SubmissionIdAndSubmittedAtAreStableAcrossAttempts()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        DateTimeOffset before = fixture.Clock.GetUtcNow();

        CountingDbContext counting = new(fixture.DbContext)
        {
            // Advance the clock between attempts, so a handler re-reading it would visibly drift.
            BeforeSave = _ => fixture.Clock.Advance(TimeSpan.FromSeconds(7)),
            FailOn = save => save == 1 ? CountingDbContext.RetryableConflict(SummaryIndex) : null
        };

        SaveDailyAttendance.Response response = await fixture.Handler(counting).Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Equal(2, counting.SaveChangesCalls);
        Assert.Equal(before, response.SubmittedAt);

        await using SparkrockRwcDbContext reader = fixture.NewContext();

        AttendanceSubmissionLog log = await reader.AttendanceSubmissionLogs.SingleAsync();

        Assert.Equal(response.SubmissionId, log.Id);
        Assert.Equal(before, log.SubmittedAt);

        // F01d R-5 accepted that SubmittedAt and created_at hold one fact; this is the mechanism by
        // which they can disagree — the interceptor restamps created_at from its own clock read on each
        // attempt, so it lands at the *retried* instant.
        Assert.Equal(before.AddSeconds(14), log.CreatedAt);
    }

    /// <summary>
    ///     The outcome map is rebuilt every attempt. A row created on attempt 1 and lost to a racer is
    ///     "updated" on attempt 2 — carrying the outcome forward would report a creation that did not
    ///     happen.
    /// </summary>
    [Fact]
    public async Task Handle_WhenRowIsCreatedOnAttemptOneAndLostToARace_ReportsUpdatedNotCreated()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        bool racerHasWritten = false;

        CountingDbContext counting = new(fixture.DbContext)
        {
            BeforeSave = save =>
            {
                if (save != 1 || racerHasWritten)
                    return;

                racerHasWritten = true;

                // Another writer lands the row first, through a second context over the same store.
                using SparkrockRwcDbContext racer = fixture.NewContext();

                racer.StudentAttendances.Add(new StudentAttendance
                {
                    Id = Guid.NewGuid(),
                    StudentId = student.Id,
                    SchoolId = Guid.NewGuid(),
                    AttendDate = AttendanceSubmissionFixture.SubmittedDate,
                    AttendanceCodeId = fixture.Codes[AttendanceSubmissionFixture.AbsentCode].Id,
                    AttendCode = AttendanceSubmissionFixture.AbsentCode,
                    AttendCodeDescription = "Racer",
                    IsAbsent = true,
                    IsExcused = false
                });

                racer.SaveChangesAsync(CancellationToken.None).GetAwaiter().GetResult();
            },
            FailOn = save => save == 1 ? CountingDbContext.RetryableConflict(AttendanceDateIndex) : null
        };

        SaveDailyAttendance.Response response = await fixture.Handler(counting).Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Equal(2, counting.SaveChangesCalls);
        Assert.Equal(SaveDailyAttendance.OutcomeUpdated, response.Entries[0].Outcome);
        Assert.Equal(0, response.CreatedCount);
        Assert.Equal(1, response.UpdatedCount);

        // One row, still — the racer's, now carrying this submission's id and this school.
        await using SparkrockRwcDbContext reader = fixture.NewContext();

        StudentAttendance row = await reader.StudentAttendances.SingleAsync();
        Assert.Equal(response.SubmissionId, row.SubmissionId);
        Assert.Equal(fixture.SchoolId, row.SchoolId);
    }

    /// <summary>
    ///     Exhaustion is a <see cref="ConflictException" />, never a
    ///     <see cref="ConcurrencyConflictException" />: the retry predicate must not be able to catch
    ///     its own terminal throw.
    /// </summary>
    [Fact]
    public async Task Handle_WhenEveryAttemptRaces_ThrowsConflictAfterMaxAttempts()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        CountingDbContext counting = new(fixture.DbContext)
        {
            FailOn = _ => CountingDbContext.RetryableConflict(AttendanceDateIndex)
        };

        ConflictException exception = await Assert.ThrowsAsync<ConflictException>(() =>
            fixture.Handler(counting).Handle(
                fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None));

        Assert.Equal(ErrorCodes.Attendance.ConcurrentSubmission, exception.ErrorCode);
        Assert.Equal(AttendanceSave.MaxAttempts, counting.SaveChangesCalls);
    }

    /// <summary>
    ///     DEC-18 and DEC-14: the alert episode exhausts to <c>ALERT.DUPLICATE_OPEN_EPISODE</c>, not to
    ///     the attendance code. Both are retryable; they report different things.
    /// </summary>
    [Fact]
    public async Task Handle_WhenTheAlertEpisodeRacesToExhaustion_ReportsTheAlertCode()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        CountingDbContext counting = new(fixture.DbContext)
        {
            FailOn = _ => CountingDbContext.RetryableConflict(
                AlertEpisodeIndex, ErrorCodes.Alert.DuplicateOpenEpisode)
        };

        ConflictException exception = await Assert.ThrowsAsync<ConflictException>(() =>
            fixture.Handler(counting).Handle(
                fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None));

        Assert.Equal(ErrorCodes.Alert.DuplicateOpenEpisode, exception.ErrorCode);
    }

    /// <summary>
    ///     A <see cref="DbUpdateConcurrencyException" /> carries no constraint name, so it is classified
    ///     by type — it can only be the summary token, which is the one entity that carries one.
    /// </summary>
    [Fact]
    public async Task Handle_WhenTheConcurrencyTokenMismatches_Retries()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        CountingDbContext counting = new(fixture.DbContext)
        {
            FailOn = save => save == 1 ? new DbUpdateConcurrencyException("Injected.") : null
        };

        SaveDailyAttendance.Response response = await fixture.Handler(counting).Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Equal(2, counting.SaveChangesCalls);
        Assert.Equal(1, response.CreatedCount);
    }

    // ------------------------------------------------------------------------- T07-16, logging

    /// <summary>
    ///     1500 fires once after the save; 1501 fires once per retry, inside the loop.
    /// </summary>
    /// <remarks>
    ///     Conventions §4's "log once" rule does not cover a retry. O-40 records that DEC-14's bound
    ///     cannot be tuned without a counter and there is no metrics pipeline, so this is the minimum
    ///     observability substitute — and it is the only handler-tier evidence that an attempt was
    ///     discarded rather than never made.
    /// </remarks>
    [Fact]
    public async Task Handle_WhenAnAttemptIsRetried_LogsOneWarningPerRetryAndOneInformationOnSuccess()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        RecordingLogger<SaveDailyAttendance.CommandHandler> logger = new();

        CountingDbContext counting = new(fixture.DbContext)
        {
            FailOn = save => save <= 2 ? CountingDbContext.RetryableConflict(SummaryIndex) : null
        };

        await fixture.Handler(counting, logger: logger).Handle(
            fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None);

        Assert.Equal([1501, 1501, 1500], logger.EventIds);
    }

    [Fact]
    public async Task Handle_WhenAttemptsAreExhausted_LogsTheExhaustionWarningAndNotTheSuccessLine()
    {
        await using AttendanceSubmissionFixture fixture = await AttendanceSubmissionFixture.CreateAsync();
        Student student = await fixture.AddStudentAsync();

        RecordingLogger<SaveDailyAttendance.CommandHandler> logger = new();

        CountingDbContext counting = new(fixture.DbContext)
        {
            FailOn = _ => CountingDbContext.RetryableConflict(SummaryIndex)
        };

        await Assert.ThrowsAsync<ConflictException>(() =>
            fixture.Handler(counting, logger: logger).Handle(
                fixture.Command(AttendanceSubmissionFixture.Entry(student.Id)), CancellationToken.None));

        Assert.Equal([1501, 1501, 1502], logger.EventIds);
        Assert.DoesNotContain(1500, logger.EventIds);
    }

    /// <summary>
    ///     Conventions §4: counts, school id and date only. Never a student identifier, never
    ///     <c>Notes</c>, never a name, never a code value.
    /// </summary>
    /// <remarks>
    ///     The vacuity guard is asserted first. A reflective sweep that finds no templates passes
    ///     silently, which is the defect class this codebase keeps reproducing — three consecutive
    ///     features recorded an <c>ErrorCodes</c> file as shipped while it did not exist, and every
    ///     assertion over it passed by having no cases.
    /// </remarks>
    [Fact]
    public void LogTemplates_CarryNoStudentIdentifierAndSitInTheAttendanceRange()
    {
        (string Method, string Template, int EventId)[] templates = typeof(SaveDailyAttendance)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(method => (Method: method, Attribute: method.GetCustomAttribute<LoggerMessageAttribute>()))
            .Where(candidate => candidate.Attribute is not null)
            .Select(candidate => (
                Method: candidate.Method.Name,
                Template: candidate.Attribute!.Message,
                candidate.Attribute.EventId))
            .ToArray();

        Assert.Equal(3, templates.Length);

        string[] banned = ["StudentId", "Notes", "FirstName", "LastName", "Grade", "AttendCode"];

        foreach ((string method, string template, int eventId) in templates)
        {
            Assert.InRange(eventId, 1500, 1599);

            foreach (string fragment in banned)
            {
                Assert.False(
                    template.Contains(fragment, StringComparison.OrdinalIgnoreCase),
                    $"{method} logs '{template}', which names '{fragment}'. A student identifier or a "
                    + "free-text field in a log line survives log retention and ships to every "
                    + "aggregator (conventions §4).");
            }
        }

        Assert.Equal([1500, 1501, 1502], templates.Select(template => template.EventId).Order());
    }
}
