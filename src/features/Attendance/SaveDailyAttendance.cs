using System.Diagnostics;
using System.Globalization;
using Carter;
using domain.Alerts;
using domain.Attendance;
using domain.Exceptions;
using domain.Schools;
using domain.Security;
using domain.ValueObjects;
using FluentValidation;
using FluentValidation.Results;
using infra.persistence.sql;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace features.Attendance;

/// <summary>
///     <c>POST /schools/{schoolId}/attendance/{date}/submissions</c> — the attendance write path.
/// </summary>
/// <remarks>
///     Replaces <c>sp_SaveDailyAttendance</c>, which as supplied <b>cannot be created</b>: line 83
///     calls an unqualified scalar UDF, T-SQL parses that as a built-in, and <c>CREATE PROCEDURE</c>
///     fails at parse time (L-13, legacy-analysis §0). Rows written by the supplied artifact: zero. So
///     there is no behaviour to reproduce and this ports <em>intent</em> (DEC-01). Fourteen divergence
///     entries name this feature, more than any other.
///     <para>
///         <b>The whole submission is one <c>SaveChangesAsync</c></b> (DEC-14). DEC-04's set-based
///         recount plus <c>SELECT … FOR UPDATE</c> is unbuildable — every raw-SQL entry point is
///         unreachable from <c>features</c> (VC-01) and EF Core 8 has no pessimistic-locking API
///         (VC-02) — but the lock was not defensive: the lost update is real and reproducible. The
///         replacement rests on the submission writing exactly one date, which is structural because
///         the date is a route segment: the prior count is read <em>excluding that date</em>, the new
///         total is computed in memory, and concurrency is handled optimistically with a bounded
///         retry.
///     </para>
///     <para>
///         <b>Nothing about the retry is verifiable at the handler tier.</b> VC-35: EF InMemory builds
///         the <c>uint</c>/<c>xmin</c> token and never populates it, so it stays zero, original always
///         equals current, and every concurrency check passes trivially. A handler-tier test asserting
///         that a race recovers passes whether or not the mechanism exists. The three races, attempt
///         exhaustion, atomicity, the global <c>(StudentId, AttendDate)</c> index and idempotency all
///         live in <c>features.integration.tests</c> and nowhere else.
///     </para>
///     <para>
///         This slice is the nominated reference for the transactional shape (design §5), so the loop
///         below is a plainly readable <c>for</c> with an explicit recovery step rather than an
///         abstraction over a retry policy.
///     </para>
/// </remarks>
public static partial class SaveDailyAttendance
{
    /// <summary>The transport-level retry key (O-09). Optional; a replay is a 409, not a replayed 201.</summary>
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>An attendance row was inserted by this submission.</summary>
    public const string OutcomeCreated = "created";

    /// <summary>An attendance row already existed for this student and date and was overwritten.</summary>
    public const string OutcomeUpdated = "updated";

    /// <summary>
    ///     The identical message for every unresolved student id, whatever the reason.
    /// </summary>
    /// <remarks>
    ///     Conventions §2's existence-oracle rule: an unknown id, an id belonging to another school and
    ///     a transferred-away student must be indistinguishable. It interpolates nothing — an echoed
    ///     Guid would confirm the id was recognised as well-formed and nothing else, and the whole
    ///     point is that the response says the same thing in all three cases.
    /// </remarks>
    public const string StudentNotOnRosterMessage = "Student is not on this school's roster.";

    /// <summary>
    ///     Constraint names whose <c>23505</c> is worth another attempt.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Duplicated here as literals because <c>features</c> cannot reference
    ///         <c>infra.persistence.postgre</c>, which is where the registry lives (VC-23:
    ///         <c>PostgresException</c> is an Npgsql type). A name that drifts from the registry's is a
    ///         silent miss of exactly the VC-36 kind — the lookup is ordinal, a miss is not an error,
    ///         and the failure is invisible until the constraint is actually violated. That is what
    ///         <c>SaveDailyAttendanceRetryTests.RetryableConstraints_MatchTheRegistrysRetryableRows</c>
    ///         exists to catch, from a project that can see both sides.
    ///     </para>
    ///     <para>
    ///         <b>The predicate matches on constraint name, never on exception base type.</b> DEC-14:
    ///         matching on <c>DbUpdateException</c> alone would retry a permanent foreign-key or check
    ///         violation until the bound is exhausted, turning a 400-shaped data error into three round
    ///         trips and a 409.
    ///     </para>
    ///     <para>
    ///         The alert-episode index is in this set on DEC-18's authority, not this feature's spec:
    ///         two concurrent submissions for one student can both decide to raise, and mapped straight
    ///         to a bare 409 the loser fails an entire attendance batch because an unrelated writer
    ///         raised the same alert first. That is the defect DEC-14 corrected for attendance. The
    ///         retry converges rather than repeating — on re-read the episode exists, so
    ///         <c>ShouldRaise</c> declines (O-52).
    ///     </para>
    /// </remarks>
    internal static readonly IReadOnlySet<string> RetryableConstraints = new HashSet<string>(StringComparer.Ordinal)
    {
        "ix_student_attendances_student_id_attend_date",
        "ix_summaries_student_id_school_year_start",
        "ix_student_alerts_open_episode"
    };

    /// <summary>
    ///     Stands in for a constraint name in the retry log when the failure was a token mismatch.
    /// </summary>
    /// <remarks>
    ///     <c>DbUpdateConcurrencyException</c> names no constraint: the cause is an affected-row count
    ///     of zero, not a server error, which is also how it survives the <c>SaveChangesAsync</c>
    ///     override untranslated (its inner exception is not a <c>PostgresException</c>).
    /// </remarks>
    internal const string ConcurrencyTokenFailure = "(concurrency token)";

    [LoggerMessage(
        EventId = 1500,
        Level = LogLevel.Information,
        Message = "Attendance submission saved. SchoolId: {SchoolId}, AttendDate: {AttendDate}, "
                  + "RecordCount: {RecordCount}, CreatedCount: {CreatedCount}, UpdatedCount: {UpdatedCount}, "
                  + "AlertsRaised: {AlertsRaised}, AlertsResolved: {AlertsResolved}, Attempts: {Attempts}")]
    private static partial void LogSubmissionSaved(
        ILogger logger,
        Guid schoolId,
        DateOnly attendDate,
        int recordCount,
        int createdCount,
        int updatedCount,
        int alertsRaised,
        int alertsResolved,
        int attempts);

    /// <summary>
    ///     One per retry, inside the loop.
    /// </summary>
    /// <remarks>
    ///     Conventions §4's "write paths log once" rule does not cover a retry, and O-40 records that
    ///     DEC-14's bound cannot be tuned without a counter while no metrics pipeline exists. A warning
    ///     per retry is the minimum substitute, and it carries no student identifier.
    /// </remarks>
    [LoggerMessage(
        EventId = 1501,
        Level = LogLevel.Warning,
        Message = "Attendance submission retried. SchoolId: {SchoolId}, AttendDate: {AttendDate}, "
                  + "Attempt: {Attempt}, ConstraintName: {ConstraintName}")]
    private static partial void LogSubmissionRetried(
        ILogger logger,
        Guid schoolId,
        DateOnly attendDate,
        int attempt,
        string constraintName);

    [LoggerMessage(
        EventId = 1502,
        Level = LogLevel.Warning,
        Message = "Attendance submission attempts exhausted. SchoolId: {SchoolId}, AttendDate: {AttendDate}")]
    private static partial void LogSubmissionAttemptsExhausted(ILogger logger, Guid schoolId, DateOnly attendDate);

    /// <summary>
    ///     Built by the endpoint from the route values, the <c>Idempotency-Key</c> header and the body.
    /// </summary>
    public sealed class Command : IRequest<Response>
    {
        /// <summary>From the route, which is authoritative; the body must not repeat it.</summary>
        public required Guid SchoolId { get; init; }

        /// <summary>
        ///     From the route, as a <c>string</c>.
        /// </summary>
        /// <remarks>
        ///     Validated rather than bound as a <see cref="DateOnly" />, so <c>2026-13-01</c> is a 400
        ///     instead of a routing 404 indistinguishable from an unknown school (conventions §2).
        ///     <para>
        ///         <b>The property name is load-bearing.</b> <c>api/Errors/ViolationSource</c> resolves a
        ///         violation's <c>source</c> by matching the camel-cased root of the property path
        ///         against the request's route values first, and <c>RouteValueDictionary</c> is
        ///         case-insensitive — so <c>Date</c> → <c>date</c> → <c>"path"</c>. This is the first
        ///         endpoint in the system to exercise that branch.
        ///     </para>
        /// </remarks>
        public required string Date { get; init; }

        /// <summary>
        ///     Optional client-supplied retry key, from the <c>Idempotency-Key</c> header (O-09).
        /// </summary>
        /// <remarks>
        ///     A header rather than a body field: route values and the body are the resource, and the
        ///     key is transport-level retry metadata. Absent means no uniqueness applies — the index is
        ///     filtered <c>WHERE idempotency_key IS NOT NULL</c>, so many nulls coexist.
        ///     <para>
        ///         A replay within the same school is a <b>409</b> and never a replayed 201: replaying
        ///         the original body needs a stored-response column F01d deliberately does not ship. A
        ///         409 still answers the question O-09 poses — "did it land?" — unambiguously.
        ///     </para>
        /// </remarks>
        public string? IdempotencyKey { get; init; }

        public required IReadOnlyList<Entry> Entries { get; init; }
    }

    /// <summary>One student's attendance for the submitted date.</summary>
    public sealed class Entry
    {
        public required Guid StudentId { get; init; }

        /// <summary>
        ///     The attendance code's <c>Value</c>, matched case-insensitively.
        /// </summary>
        /// <remarks>
        ///     V-27: SQL Server's collation made <c>A</c> and <c>a</c> one code, and Postgres unique
        ///     indexes are case-sensitive. The handler upper-cases before an ordinal lookup, so the
        ///     legacy meaning is preserved; without that, <c>"a"</c> would be an unknown code.
        /// </remarks>
        public required string AttendCode { get; init; }

        public int? MinutesLate { get; init; }

        /// <summary>
        ///     Free text, stored verbatim and never echoed.
        /// </summary>
        /// <remarks>
        ///     Not inspected, not sanitised, not silently truncated — length-bounded by the validator
        ///     and by the column (DEC-06) and otherwise stored as sent. There is no SQL to inject into
        ///     (VC-01, V-05). It appears in no response body, no violation message and no log template
        ///     (conventions §2, §4).
        /// </remarks>
        public string? Notes { get; init; }
    }

    /// <summary>
    ///     The JSON payload. Separate from <see cref="Command" /> because the command additionally
    ///     carries route values and a header, and conventions §2 forbids a body repeating either.
    /// </summary>
    public sealed class Body
    {
        public IReadOnlyList<Entry>? Entries { get; init; }
    }

    internal sealed class CommandValidator : AbstractValidator<Command>
    {
        public CommandValidator()
        {
            RuleFor(command => command.Date)
                .Must(date => GetAttendanceRoster.TryParseDate(date, out _))
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage(FormattableString.Invariant(
                    $"Date must be an ISO 8601 date, {GetAttendanceRoster.DateFormat}."));

            RuleFor(command => command.Entries)
                .Cascade(CascadeMode.Stop)
                .NotEmpty()
                .WithErrorCode(ErrorCodes.Validation.RequiredField)
                .WithMessage("At least one entry is required.")
                .Must(entries => entries.Count <= AttendanceSave.MaxBatchSize)
                .WithErrorCode(ErrorCodes.Attendance.BatchSizeExceeded)
                .WithMessage(FormattableString.Invariant(
                    $"A submission may carry at most {AttendanceSave.MaxBatchSize} entries."));

            // V-15. A validator rule rather than a handler rule for two reasons: the +1 would be
            // applied twice to one total, and EF would hold two Added rows for one
            // (StudentId, AttendDate) — a 23505 the retry loop would treat as a race and retry twice
            // before failing. Rejecting it at the boundary is cheaper and honest.
            RuleFor(command => command.Entries).Custom(AddDuplicateStudentFailures);

            RuleForEach(command => command.Entries).ChildRules(entry =>
            {
                entry.RuleFor(item => item.StudentId)
                    .NotEqual(Guid.Empty)
                    .WithErrorCode(ErrorCodes.Validation.RequiredField)
                    .WithMessage("Student id is required.");

                // Length only. A pattern rule here would be a second way to say "no such code", and a
                // typo and an unknown code must read the same (L-06, V-04).
                entry.RuleFor(item => item.AttendCode)
                    .Cascade(CascadeMode.Stop)
                    .NotEmpty()
                    .WithErrorCode(ErrorCodes.Validation.RequiredField)
                    .WithMessage("Attendance code is required.")
                    .MaximumLength(AttendanceSave.MaxAttendCodeLength)
                    .WithErrorCode(ErrorCodes.Validation.Failed)
                    .WithMessage(FormattableString.Invariant(
                        $"Attendance code must not exceed {AttendanceSave.MaxAttendCodeLength} characters."));

                // Mirrors ck_student_attendances_minutes_late_not_negative.
                entry.RuleFor(item => item.MinutesLate)
                    .GreaterThanOrEqualTo(0)
                    .WithErrorCode(ErrorCodes.Validation.Failed)
                    .WithMessage("Minutes late must not be negative.")
                    .When(item => item.MinutesLate.HasValue);

                // The message names the bound and never the value. FluentValidation's default English
                // for MaximumLength interpolates {PropertyValue}, and Notes routinely carries health
                // and safeguarding detail — api/Errors/ViolationMessage would redact it, but a rule
                // that relies on being redacted is a rule that leaks the day the leaf name changes.
                entry.RuleFor(item => item.Notes)
                    .MaximumLength(AttendanceSave.MaxNotesLength)
                    .WithErrorCode(ErrorCodes.Validation.Failed)
                    .WithMessage(FormattableString.Invariant(
                        $"Notes must not exceed {AttendanceSave.MaxNotesLength} characters."));
            });
        }

        /// <summary>
        ///     One failure per occurrence <em>after</em> the first, so the path names the repeat rather
        ///     than the original.
        /// </summary>
        private static void AddDuplicateStudentFailures(IReadOnlyList<Entry> entries, ValidationContext<Command> context)
        {
            if (entries is null)
                return;

            HashSet<Guid> seen = [];

            for (int index = 0; index < entries.Count; index++)
            {
                if (seen.Add(entries[index].StudentId))
                    continue;

                context.AddFailure(new ValidationFailure(
                    FormattableString.Invariant($"Entries[{index}].{nameof(Entry.StudentId)}"),
                    "The same student appears more than once in this submission.")
                {
                    ErrorCode = ErrorCodes.Attendance.DuplicateStudent
                });
            }
        }
    }

    /// <summary>
    ///     What the submission recorded.
    /// </summary>
    /// <remarks>
    ///     <c>notes</c> and <c>minutesLate</c> appear nowhere in here. <c>notes</c> because conventions
    ///     §2 keeps free text out of every channel the caller did not ask for, and <c>minutesLate</c>
    ///     because design §4's response contract does not carry it — adding a field to a canonical wire
    ///     shape is not this feature's to do unilaterally (plan, conflict 9).
    ///     <para>
    ///         <c>thresholdSourceSchoolId</c> is never returned (DEC-16): it is the student's current
    ///         school, and returning it to a former school discloses where a child moved to. Only the
    ///         threshold <em>value</em> appears. The <c>thresholdSource</c> discriminator is omitted
    ///         too — DEC-08 makes both sources the same school on this endpoint, and F09 owns it.
    ///     </para>
    /// </remarks>
    public sealed record Response
    {
        public required Guid SubmissionId { get; init; }

        public required Guid SchoolId { get; init; }

        public required DateOnly AttendanceDate { get; init; }

        public required int SchoolYear { get; init; }

        public required string SchoolYearLabel { get; init; }

        /// <summary>Null when no <em>active</em> term covers the date — preserved from legacy (D-03).</summary>
        public Guid? TermId { get; init; }

        public required DateTimeOffset SubmittedAt { get; init; }

        public required SubmittedByInfo SubmittedBy { get; init; }

        public required int RecordCount { get; init; }

        public required int CreatedCount { get; init; }

        public required int UpdatedCount { get; init; }

        /// <summary>
        ///     One element per submitted entry, in request order.
        /// </summary>
        /// <remarks>
        ///     <b>Keyed by <c>studentId</c>, never by array index.</b> The order is for readability; the
        ///     contract is that clients match on the id. A client that reorders its grid between render
        ///     and submit would otherwise map results to the wrong students — and these results carry
        ///     safeguarding-relevant absence totals.
        /// </remarks>
        public required IReadOnlyList<EntryResult> Entries { get; init; }

        public required AlertSummary Alerts { get; init; }

        public sealed record SubmittedByInfo
        {
            public required Guid UserId { get; init; }

            public required string DisplayName { get; init; }
        }

        public sealed record EntryResult
        {
            public required Guid StudentId { get; init; }

            public required Guid AttendanceId { get; init; }

            /// <summary><see cref="OutcomeCreated" /> or <see cref="OutcomeUpdated" />.</summary>
            public required string Outcome { get; init; }

            /// <summary>The normalised code that was stored, not the one that was sent.</summary>
            public required string AttendCode { get; init; }

            public required string AttendCodeDescription { get; init; }

            public required bool IsAbsent { get; init; }

            public required bool IsExcused { get; init; }

            /// <summary>
            ///     Absences for the school year <b>across schools</b> (V-07c ●), including this
            ///     submission.
            /// </summary>
            /// <remarks>
            ///     A cross-school figure, so this response body is a second surface for the disclosure
            ///     Q-05 asks about, alongside F09's aggregate. A "no" answer to Q-05 removes this field.
            /// </remarks>
            public required int TotalAbsences { get; init; }
        }

        /// <summary>Both lists are <c>[]</c> when empty — never absent, never null (conventions §2).</summary>
        public sealed record AlertSummary
        {
            public required IReadOnlyList<RaisedAlert> Raised { get; init; }

            public required IReadOnlyList<ResolvedAlert> Resolved { get; init; }
        }

        public sealed record RaisedAlert
        {
            public required Guid AlertId { get; init; }

            public required Guid StudentId { get; init; }

            public required int AbsenceCount { get; init; }

            /// <summary>The resolved threshold value only. Never the school it came from (DEC-16).</summary>
            public required int Threshold { get; init; }
        }

        public sealed record ResolvedAlert
        {
            public required Guid AlertId { get; init; }

            public required Guid StudentId { get; init; }

            /// <summary>The <see cref="ResolutionSource" /> member name.</summary>
            public required string Source { get; init; }
        }
    }

    /// <summary>The four code fields snapshotted onto every attendance row (D-02, V-23).</summary>
    internal sealed record CodeSnapshot(Guid Id, string Value, string Description, bool IsAbsent, bool IsExcused);

    /// <summary>
    ///     Everything that is resolved once and reused by every attempt.
    /// </summary>
    /// <remarks>
    ///     Re-reading reference data per attempt costs three extra round trips and changes nothing —
    ///     the races are on transactional rows. <c>SubmissionId</c> and <c>SubmittedAt</c> are here
    ///     because they must <em>not</em> move between attempts: the <c>Location</c> header would
    ///     otherwise point somewhere else on a retry, and the recorded time would drift by the retry
    ///     duration.
    /// </remarks>
    private sealed class SaveContext
    {
        public required Command Request { get; init; }

        public required School School { get; init; }

        public required DateOnly AttendDate { get; init; }

        public required SchoolYear SchoolYear { get; init; }

        public required Guid? TermId { get; init; }

        public required IReadOnlyCollection<Guid> SubmittedIds { get; init; }

        public required Dictionary<string, CodeSnapshot> ResolvedCodes { get; init; }

        public required Guid SubmissionId { get; init; }

        public required DateTimeOffset SubmittedAt { get; init; }

        public required AttendanceSubmissionLog Log { get; init; }

        public Guid SchoolId => Request.SchoolId;
    }

    /// <summary>
    ///     What one attempt added or mutated, so a discarded attempt can be undone.
    /// </summary>
    /// <remarks>
    ///     <c>IDbContext</c> exposes no <c>ChangeTracker</c> and no <c>Entry()</c> (VC-29), so the
    ///     handler cannot ask EF what it is tracking. It keeps its own list instead — which is also why
    ///     recovery does not depend on <c>DbUpdateException.Entries</c> being complete, a property
    ///     VC-29 pins only for a three-entity batch (plan R-2).
    /// </remarks>
    private sealed class AttemptState
    {
        public List<StudentAttendance> AddedAttendance { get; } = [];

        public List<StudentAttendanceSummary> AddedSummaries { get; } = [];

        public List<StudentAlert> AddedAlerts { get; } = [];

        /// <summary>Summaries this attempt changed, with the values they held before it did.</summary>
        public List<(StudentAttendanceSummary Summary, int TotalAbsences, Guid SchoolId)> UpdatedSummaries { get; } = [];

        /// <summary>Open episodes this attempt auto-resolved.</summary>
        public List<StudentAlert> ResolvedAlerts { get; } = [];
    }

    /// <summary>A failure the loop is willing to try again, and what to report if the bound runs out.</summary>
    private sealed record RetryableFailure(
        string ConstraintName,
        string ErrorCode,
        string Message,
        IReadOnlyList<EntityEntry> Entries);

    internal sealed class CommandHandler(
        IDbContext dbContext,
        ICurrentUser currentUser,
        TimeProvider timeProvider,
        IOptions<AttendanceSaveOptions> options,
        ILogger<CommandHandler> logger)
        : IRequestHandler<Command, Response>
    {
        public async Task<Response> Handle(Command request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!GetAttendanceRoster.TryParseDate(request.Date, out DateOnly attendDate))
            {
                // Unreachable through the pipeline: ValidationBehavior runs CommandValidator first and
                // the same TryParseDate is the rule. Stated rather than swallowed — a handler that
                // substituted a date would write attendance to the wrong day.
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"Date must be validated as {GetAttendanceRoster.DateFormat} before the handler runs."));
            }

            // ---- B. The addressed resource. Each throws immediately; none accumulates -------------
            //
            // Conventions §2: "status is decided by the addressed resource, never by an accumulated
            // item". If these joined the accumulator, a submission to a nonexistent school carrying a
            // bad code would have to choose between 404 and 400, and either choice is wrong.

            // Scope before existence, so an out-of-scope school and an absent one are indistinguishable.
            // NotFoundException takes no message parameter, which makes the payloads byte-identical by
            // construction rather than by call-site discipline.
            currentUser.EnsureAuthorized(request.SchoolId, ErrorCodes.School.NotFound);

            School? school = await dbContext.Schools
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == request.SchoolId, cancellationToken);

            if (school is null)
                throw new NotFoundException(ErrorCodes.School.NotFound);

            // V-14 ●, school half. 409 rather than 400 because the school is the addressed resource.
            // Reading an inactive school is still a 200 (DEC-19); only the write is refused.
            if (!school.IsActive)
            {
                throw new ConflictException(
                    ErrorCodes.School.Inactive,
                    "The school is deactivated and cannot accept attendance.");
            }

            EnsureDateIsInRange(attendDate, school, options.Value.BackDatingWindowDays);
            EnsureIdempotencyKeyIsBounded(request.IdempotencyKey);

            SchoolYear schoolYear = SchoolYear.FromLocalDate(attendDate);

            // D-03, preserved: no active term covering the date is not an error, it is a null TermId.
            // Ordered as a determinism backstop — V-19 makes at most one active term match, but
            // imported data predates V-19 and nothing re-checks it.
            Guid? termId = await dbContext.SchoolTerms
                .AsNoTracking()
                .Where(term => term.SchoolId == request.SchoolId
                               && term.IsActive
                               && term.StartDate <= attendDate
                               && term.EndDate >= attendDate)
                .OrderBy(term => term.StartDate)
                .ThenBy(term => term.Id)
                .Select(term => (Guid?)term.Id)
                .FirstOrDefaultAsync(cancellationToken);

            // ---- C. The body. Both queries always run; violations accumulate into one 400 ----------
            //
            // Design §4: staging these means a form with a bad student *and* a bad code takes three
            // round trips to fix. Two queries regardless of how many defects there are.

            Guid[] submittedIds = [.. request.Entries.Select(entry => entry.StudentId).Distinct()];

            HashSet<Guid> resolvedStudents =
            [
                .. await dbContext.Students
                    .AsNoTracking()

                    // No IsActive predicate, deliberately (spec §8). legacy-analysis §4 lists
                    // "attendance accepted for inactive students" as a preserved behaviour: a student
                    // deactivated mid-year still needs corrections and back-fill, and rejecting them
                    // would be a user-visible change with no divergence-log row.
                    .Where(student => submittedIds.Contains(student.Id) && student.SchoolId == request.SchoolId)
                    .Select(student => student.Id)
                    .ToListAsync(cancellationToken)
            ];

            HashSet<string> submittedCodes =
                [.. request.Entries.Select(entry => AttendanceCodes.AttendanceCodeValue.Normalise(entry.AttendCode))];

            // Codes are global, not school-scoped (conventions §1), so there is no tenant term here.
            // Unknown and inactive fall out of this one set difference, which is what makes their
            // violations identical rather than merely similar.
            Dictionary<string, CodeSnapshot> resolvedCodes = await dbContext.AttendanceCodes
                .AsNoTracking()
                .Where(code => submittedCodes.Contains(code.Value) && code.IsActive)
                .Select(code => new CodeSnapshot(
                    code.Id, code.Value, code.Description, code.IsAbsent, code.IsExcused))
                .ToDictionaryAsync(snapshot => snapshot.Value, StringComparer.Ordinal, cancellationToken);

            EnsureBodyResolves(request, resolvedStudents, resolvedCodes);

            // ---- The bounded retry (DEC-14) --------------------------------------------------------

            Guid submissionId = Guid.NewGuid();
            DateTimeOffset submittedAt = timeProvider.GetUtcNow();

            AttendanceSubmissionLog log = new()
            {
                Id = submissionId,
                SchoolId = request.SchoolId,
                AttendDate = attendDate,
                SubmittedAt = submittedAt,
                RecordCount = request.Entries.Count,
                SubmittedBy = currentUser.UserId,
                IdempotencyKey = request.IdempotencyKey
            };

            // Tracked Added once, before the loop, and never detached by recovery. Its id is the
            // Location target, so it must survive a retry unchanged (O-01).
            dbContext.AttendanceSubmissionLogs.Add(log);

            SaveContext context = new()
            {
                Request = request,
                School = school,
                AttendDate = attendDate,
                SchoolYear = schoolYear,
                TermId = termId,
                SubmittedIds = submittedIds,
                ResolvedCodes = resolvedCodes,
                SubmissionId = submissionId,
                SubmittedAt = submittedAt,
                Log = log
            };

            for (int attempt = 1; attempt <= AttendanceSave.MaxAttempts; attempt++)
            {
                // Rebuilt from scratch every attempt. A row created on attempt 1 and lost to a race is
                // "updated" on attempt 2; carrying the outcome forward reports a creation that did not
                // happen.
                AttemptState state = new();

                try
                {
                    Response response = await RunAttemptAsync(context, state, cancellationToken);

                    LogSubmissionSaved(
                        logger,
                        context.SchoolId,
                        attendDate,
                        response.RecordCount,
                        response.CreatedCount,
                        response.UpdatedCount,
                        response.Alerts.Raised.Count,
                        response.Alerts.Resolved.Count,
                        attempt);

                    return response;
                }
                catch (Exception exception) when (Classify(exception) is { } failure)
                {
                    if (attempt == AttendanceSave.MaxAttempts)
                    {
                        LogSubmissionAttemptsExhausted(logger, context.SchoolId, attendDate);

                        // ConflictException, not ConcurrencyConflictException: the retry predicate must
                        // not be able to catch its own terminal throw.
                        throw new ConflictException(failure.ErrorCode, failure.Message);
                    }

                    LogSubmissionRetried(logger, context.SchoolId, attendDate, attempt, failure.ConstraintName);

                    await RecoverAsync(failure.Entries, context, state, cancellationToken);
                }
            }

            throw new UnreachableException(
                "The attempt loop either returns or throws on the last attempt.");
        }

        /// <summary>
        ///     DEC-12's bound, resolved in the school's timezone.
        /// </summary>
        /// <remarks>
        ///     <b>In the handler, not the validator</b>, contradicting design §4's diagram — which puts
        ///     "date bounded (V-25, DEC-12)" in the FluentValidation stage annotated "before any
        ///     database work". That is not implementable: school-local requires
        ///     <c>School.TimeZoneId</c>, which requires the school row. Syntax stays in the validator;
        ///     the bound lands here, immediately after the school lookup.
        ///     <para>
        ///         It throws one violation rather than accumulating with the body checks. If the date is
        ///         refused there is no school year to evaluate the entries against, and reporting
        ///         per-entry problems computed against a year that will not be written is worse than
        ///         reporting nothing.
        ///     </para>
        ///     <para>
        ///         An unvalidated <c>TimeZoneId</c> throws <c>TimeZoneNotFoundException</c> from here and
        ///         is deliberately <b>not</b> caught (plan R-10): a data defect surfacing as a 500 is
        ///         preferable to an error code implying the caller did something wrong. F01c's own risk
        ///         list records that IANA validation is F02's.
        ///     </para>
        /// </remarks>
        private void EnsureDateIsInRange(DateOnly attendDate, School school, int backDatingWindowDays)
        {
            TimeZoneInfo timeZone = TimeZoneInfo.FindSystemTimeZoneById(school.TimeZoneId);

            DateOnly schoolLocalToday = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), timeZone).DateTime);

            DateOnly earliest = schoolLocalToday.AddDays(-backDatingWindowDays);

            if (attendDate <= schoolLocalToday && attendDate >= earliest)
                return;

            // The message names the two bounds and no free text. Both are dates, which conventions §2
            // permits a message to echo, and without them the clerk is told only "no".
            string message = FormattableString.Invariant(
                $"Attendance may be recorded from {Iso(earliest)} to {Iso(schoolLocalToday)} inclusive, in the school's local time.");

            throw new BusinessRuleException(
                ErrorCodes.Attendance.DateOutOfRange,
                [
                    new Violation(
                        ViolationSources.Path,
                        nameof(Command.Date),
                        ErrorCodes.Attendance.DateOutOfRange,
                        message)
                ]);
        }

        /// <summary>
        ///     The one bound the validator cannot own.
        /// </summary>
        /// <remarks>
        ///     <c>api/Errors/ViolationSource</c> documents that <c>header</c> is never inferred —
        ///     guessing a header from a property name invents information — so a FluentValidation rule
        ///     on <c>IdempotencyKey</c> would emit <c>"source": "body"</c> for a value that was never in
        ///     the body. <c>DomainExceptionHandler</c> writes <c>Violation.Source</c> verbatim, so a
        ///     hand-constructed violation carries the truth with no change to <c>api</c>.
        /// </remarks>
        private static void EnsureIdempotencyKeyIsBounded(string? idempotencyKey)
        {
            if (idempotencyKey is null || idempotencyKey.Length <= AttendanceSave.MaxIdempotencyKeyLength)
                return;

            throw new BusinessRuleException(
                ErrorCodes.Validation.Failed,
                [
                    new Violation(
                        ViolationSources.Header,
                        IdempotencyKeyHeader,
                        ErrorCodes.Validation.Failed,
                        FormattableString.Invariant(
                            $"{IdempotencyKeyHeader} must not exceed {AttendanceSave.MaxIdempotencyKeyLength} characters."))
                ]);
        }

        /// <summary>
        ///     Spec §2's stage C: two set differences, merged, sorted, and reported together.
        /// </summary>
        /// <remarks>
        ///     There is exactly <b>one</b> rejection branch per set, so conventions §2's
        ///     existence-oracle rule holds by construction rather than by discipline: an unknown student
        ///     id, another school's student and a student who has transferred away all pass through the
        ///     same <c>Add</c>, and so do an unknown code and an inactive one.
        ///     <para>
        ///         Sorted by entry index, then by field name, so the response order is deterministic and
        ///         the byte-identity assertions elsewhere are stable.
        ///     </para>
        /// </remarks>
        private static void EnsureBodyResolves(
            Command request,
            HashSet<Guid> resolvedStudents,
            Dictionary<string, CodeSnapshot> resolvedCodes)
        {
            List<(int Index, string Field, Violation Violation)> pending = [];

            for (int index = 0; index < request.Entries.Count; index++)
            {
                Entry entry = request.Entries[index];

                if (!resolvedStudents.Contains(entry.StudentId))
                {
                    pending.Add((index, nameof(Entry.StudentId), new Violation(
                        ViolationSources.Body,
                        FormattableString.Invariant($"Entries[{index}].{nameof(Entry.StudentId)}"),
                        ErrorCodes.Attendance.StudentNotOnRoster,
                        StudentNotOnRosterMessage)));
                }

                string code = AttendanceCodes.AttendanceCodeValue.Normalise(entry.AttendCode);

                if (!resolvedCodes.ContainsKey(code))
                {
                    // The code itself is echoed — a bounded structured value conventions §2 permits,
                    // capped at five characters by the validator. Notes never is.
                    pending.Add((index, nameof(Entry.AttendCode), new Violation(
                        ViolationSources.Body,
                        FormattableString.Invariant($"Entries[{index}].{nameof(Entry.AttendCode)}"),
                        ErrorCodes.Attendance.UnknownCode,
                        FormattableString.Invariant(
                            $"Attendance code '{code}' does not exist or is inactive."))));
                }
            }

            if (pending.Count == 0)
                return;

            throw new BusinessRuleException(
                ErrorCodes.Attendance.SubmissionRejected,
                [
                    .. pending
                        .OrderBy(item => item.Index)
                        .ThenBy(item => item.Field, StringComparer.Ordinal)
                        .Select(item => item.Violation)
                ]);
        }

        /// <summary>
        ///     One attempt: four reads, the derivation, and exactly one <c>SaveChangesAsync</c>.
        /// </summary>
        private async Task<Response> RunAttemptAsync(
            SaveContext context,
            AttemptState state,
            CancellationToken cancellationToken)
        {
            // 1. Prior counts — excluding the submitted date, which is the whole of DEC-14, and with no
            //    school predicate, which is the whole of V-07c. Students with no prior absence are
            //    absent from the dictionary, so every read below is a TryGetValue.
            Dictionary<Guid, int> prior = await AbsenceRecount
                .PriorAbsenceCounts(
                    dbContext.StudentAttendances, context.SubmittedIds, context.SchoolYear, context.AttendDate)
                .ToDictionaryAsync(count => count.StudentId, count => count.Count, cancellationToken);

            // 2. Existing attendance for the submitted date. Tracked, because these rows are updated.
            //
            //    Deliberately NOT filtered by school. V-06 keeps legacy's dedup key at one record per
            //    student per day *globally*, and DEC-08 has already established that every submitted
            //    student currently belongs to this school. A row another school wrote for today is
            //    therefore either a race or the tail of a transfer, and in both cases the current
            //    school's submission is the authority — which is also what makes DEC-14's third race
            //    recoverable: on attempt 2 the racing row is found here and updated, so the whole batch
            //    saves instead of one student 409-ing all twenty-eight.
            Dictionary<Guid, StudentAttendance> existing = (await dbContext.StudentAttendances
                    .Where(attendance => context.SubmittedIds.Contains(attendance.StudentId)
                                         && attendance.AttendDate == context.AttendDate)
                    .ToListAsync(cancellationToken))
                .ToDictionary(attendance => attendance.StudentId);

            // 3. Summaries. Whole-value comparison on SchoolYearStart only — reaching into
            //    .StartYear does not translate and is a runtime 500, not a compile error (VC-31).
            Dictionary<Guid, StudentAttendanceSummary> summaries = (await dbContext.StudentAttendanceSummaries
                    .Where(summary => context.SubmittedIds.Contains(summary.StudentId)
                                      && summary.SchoolYearStart == context.SchoolYear)
                    .ToListAsync(cancellationToken))
                .ToDictionary(summary => summary.StudentId);

            // 4. Open episodes and manual resolutions, in one round trip over the four-column key.
            //    SchoolId is part of both keys and that is the entire point of DEC-16: keyed
            //    school-agnostically, a former school's open alert blocked the receiving school from
            //    raising one it could neither see nor resolve, and a former school's manual resolution
            //    suppressed alerting at the new school for the rest of the year.
            //
            //    Entities rather than a projection, because the auto-resolve branch mutates the episode
            //    it finds. A soft-deleted alert is hidden by the reflective filter, so a soft-deleted
            //    manual resolution stops suppressing — the intended reading of DEC-18's is_deleted term.
            List<StudentAlert> alerts = await dbContext.StudentAlerts
                .Where(alert => context.SubmittedIds.Contains(alert.StudentId)
                                && alert.SchoolId == context.SchoolId
                                && alert.AlertType == AlertType.ChronicAbsence
                                && alert.SchoolYearStart == context.SchoolYear
                                && (alert.ResolvedAt == null
                                    || alert.ResolutionSource == ResolutionSource.Manual))
                .ToListAsync(cancellationToken);

            int createdCount = 0;
            int updatedCount = 0;
            List<Response.EntryResult> results = new(context.Request.Entries.Count);
            List<Response.RaisedAlert> raised = [];
            List<Response.ResolvedAlert> resolved = [];

            foreach (Entry entry in context.Request.Entries)
            {
                CodeSnapshot code = context.ResolvedCodes[
                    AttendanceCodes.AttendanceCodeValue.Normalise(entry.AttendCode)];

                prior.TryGetValue(entry.StudentId, out int priorAbsences);
                int total = priorAbsences + (code.IsAbsent ? 1 : 0);

                // Each entry is resolved independently, and no value computed for one entry is visible
                // to the next. That is the whole of V-01 and V-02: legacy's @ExistingID was never reset
                // between cursor iterations, so once any student in the batch had a row every later
                // student re-UPDATEd *that student's* row; and its @IsAbsent/@IsExcused went stale on an
                // unrecognised code, leaking the previous student's flags.
                string outcome = UpsertAttendance(context, state, entry, code, existing);

                if (string.Equals(outcome, OutcomeCreated, StringComparison.Ordinal))
                    createdCount++;
                else
                    updatedCount++;

                UpsertSummary(context, state, entry.StudentId, total, summaries);
                EvaluateAlerts(context, state, entry.StudentId, total, alerts, raised, resolved);

                results.Add(new Response.EntryResult
                {
                    StudentId = entry.StudentId,
                    AttendanceId = existing[entry.StudentId].Id,
                    Outcome = outcome,
                    AttendCode = code.Value,
                    AttendCodeDescription = code.Description,
                    IsAbsent = code.IsAbsent,
                    IsExcused = code.IsExcused,
                    TotalAbsences = total
                });
            }

            // The one save. Attendance rows, summaries, alerts and the log go together under EF's
            // implicit transaction (VC-32), so a 23505 on any of them rolls back the rest. There is no
            // BeginTransactionAsync anywhere in this slice and no 207: partial success is not
            // representable, which is the point.
            await dbContext.SaveChangesAsync(cancellationToken);

            return new Response
            {
                SubmissionId = context.SubmissionId,
                SchoolId = context.SchoolId,
                AttendanceDate = context.AttendDate,
                SchoolYear = context.SchoolYear.StartYear,
                SchoolYearLabel = context.SchoolYear.ToString(),
                TermId = context.TermId,
                SubmittedAt = context.SubmittedAt,
                SubmittedBy = new Response.SubmittedByInfo
                {
                    UserId = currentUser.UserId,
                    DisplayName = currentUser.DisplayName
                },
                RecordCount = context.Request.Entries.Count,
                CreatedCount = createdCount,
                UpdatedCount = updatedCount,
                Entries = results,
                Alerts = new Response.AlertSummary { Raised = raised, Resolved = resolved }
            };
        }

        /// <summary>
        ///     Inserts or overwrites one student's row for the date, and returns which happened.
        /// </summary>
        /// <remarks>
        ///     There is no delete branch. An omitted student is untouched, not defaulted to present and
        ///     not deleted (D-08, V-20) — a submission is a partial upsert over the students it lists.
        ///     <para>
        ///         The four snapshot fields are rewritten on update as well as on insert. "Write-once at
        ///         save" (D-02, V-23) is per save, not per row lifetime: the row records what was
        ///         recorded, and a correction records something else.
        ///     </para>
        /// </remarks>
        private string UpsertAttendance(
            SaveContext context,
            AttemptState state,
            Entry entry,
            CodeSnapshot code,
            Dictionary<Guid, StudentAttendance> existing)
        {
            if (!existing.TryGetValue(entry.StudentId, out StudentAttendance? row))
            {
                row = new StudentAttendance
                {
                    Id = Guid.NewGuid(),
                    StudentId = entry.StudentId,
                    SchoolId = context.SchoolId,
                    AttendDate = context.AttendDate,
                    TermId = context.TermId,
                    AttendanceCodeId = code.Id,
                    SubmissionId = context.SubmissionId,
                    AttendCode = code.Value,
                    AttendCodeDescription = code.Description,
                    IsAbsent = code.IsAbsent,
                    IsExcused = code.IsExcused,
                    MinutesLate = entry.MinutesLate,
                    Notes = entry.Notes
                };

                dbContext.StudentAttendances.Add(row);
                state.AddedAttendance.Add(row);
                existing[entry.StudentId] = row;

                return OutcomeCreated;
            }

            // The row may have been written by another school (V-06's global key). The submitting
            // school becomes the school of record for it, matching what SubmissionId already means:
            // "which submission last wrote this row".
            row.SchoolId = context.SchoolId;
            row.TermId = context.TermId;
            row.AttendanceCodeId = code.Id;

            // O-01. Set on updated rows as well as created ones — the update branch is the one that
            // gets forgotten, and F11 enumerates a submission's rows through this column.
            row.SubmissionId = context.SubmissionId;

            row.AttendCode = code.Value;
            row.AttendCodeDescription = code.Description;
            row.IsAbsent = code.IsAbsent;
            row.IsExcused = code.IsExcused;
            row.MinutesLate = entry.MinutesLate;
            row.Notes = entry.Notes;

            // Audit fields are never assigned here — the interceptor owns them (DEC-21).
            return OutcomeUpdated;
        }

        /// <summary>
        ///     Creates the summary row, or updates it only when a value actually changes.
        /// </summary>
        /// <remarks>
        ///     Created even at <c>TotalAbsences = 0</c>, so F09 never has to distinguish "no row" from
        ///     "zero" and the row exists the moment the student's first absence lands.
        ///     <para>
        ///         An <em>unconditional</em> update would stamp <c>ModifiedAt</c> through the
        ///         interceptor for a write that did not happen, and would burn the <c>xmin</c> token on
        ///         a no-op — manufacturing contention out of nothing on exactly the row the retry loop
        ///         contends over.
        ///     </para>
        ///     <para>
        ///         <c>SchoolId</c> follows the submitting school, closing F01d's R-6. DEC-08 guarantees
        ///         that school is the student's current one; leaving it stale strands a transferred
        ///         student on the former school's F09 list for the rest of the year while never
        ///         appearing on the receiving school's.
        ///     </para>
        /// </remarks>
        private void UpsertSummary(
            SaveContext context,
            AttemptState state,
            Guid studentId,
            int total,
            Dictionary<Guid, StudentAttendanceSummary> summaries)
        {
            if (!summaries.TryGetValue(studentId, out StudentAttendanceSummary? summary))
            {
                summary = new StudentAttendanceSummary
                {
                    Id = Guid.NewGuid(),
                    StudentId = studentId,
                    SchoolId = context.SchoolId,
                    SchoolYearStart = context.SchoolYear,
                    TotalAbsences = total
                };

                dbContext.StudentAttendanceSummaries.Add(summary);
                state.AddedSummaries.Add(summary);
                summaries[studentId] = summary;

                return;
            }

            if (summary.TotalAbsences == total && summary.SchoolId == context.SchoolId)
                return;

            state.UpdatedSummaries.Add((summary, summary.TotalAbsences, summary.SchoolId));

            summary.TotalAbsences = total;
            summary.SchoolId = context.SchoolId;
        }

        /// <summary>
        ///     Applies DEC-18's lifecycle to one student, using the shipped <c>AlertRules</c> predicates.
        /// </summary>
        /// <remarks>
        ///     The two branches are independent <c>if</c>s rather than an <c>if</c>/<c>else</c> on
        ///     purpose. They are already mutually exclusive — <c>ShouldRaise</c> requires no open
        ///     episode and <c>ShouldAutoResolve</c> requires one — and expressing that in the control
        ///     flow would hide a future edit that broke it.
        ///     <c>Handle_NeverRaisesAndAutoResolvesTheSameStudentInOneSave</c> is the assertion.
        /// </remarks>
        private void EvaluateAlerts(
            SaveContext context,
            AttemptState state,
            Guid studentId,
            int total,
            List<StudentAlert> alerts,
            List<Response.RaisedAlert> raised,
            List<Response.ResolvedAlert> resolved)
        {
            StudentAlert? openEpisode = alerts.FirstOrDefault(
                alert => alert.StudentId == studentId && alert.ResolvedAt is null);

            bool hasManualResolutionThisYear = alerts.Exists(
                alert => alert.StudentId == studentId
                         && alert.ResolvedAt is not null
                         && alert.ResolutionSource == ResolutionSource.Manual);

            // DEC-16, V-17: the governing threshold is the *student's current school's*, and on this
            // endpoint DEC-08 has already made that the submitting school — so the two coincide here by
            // construction, which is why no thresholdSource discriminator is emitted (F09 owns it).
            int? schoolThreshold = context.School.AbsenceAlertThreshold;
            bool hasOpenEpisode = openEpisode is not null;

            if (AlertRules.ShouldRaise(total, schoolThreshold, hasOpenEpisode, hasManualResolutionThisYear))
            {
                int threshold = AbsenceRules.ResolveThreshold(schoolThreshold);

                StudentAlert alert = new()
                {
                    Id = Guid.NewGuid(),
                    StudentId = studentId,
                    SchoolId = context.SchoolId,
                    AlertType = AlertType.ChronicAbsence,
                    SchoolYearStart = context.SchoolYear,
                    AbsenceCount = total,

                    // Audit only. Every comparison uses the school's current threshold, so changing a
                    // threshold does not retroactively re-evaluate anyone (DEC-18); F10's triage query
                    // is what lists the alerts a threshold change stranded.
                    ThresholdAtRaise = threshold
                };

                dbContext.StudentAlerts.Add(alert);
                state.AddedAlerts.Add(alert);
                alerts.Add(alert);

                raised.Add(new Response.RaisedAlert
                {
                    AlertId = alert.Id,
                    StudentId = studentId,
                    AbsenceCount = total,
                    Threshold = threshold
                });
            }

            if (!AlertRules.ShouldAutoResolve(total, schoolThreshold, hasOpenEpisode))
                return;

            // ck_student_alerts_resolution_consistent requires (resolved_at IS NULL) = (resolution_source
            // IS NULL). The reason stays null because the source already says what happened.
            openEpisode!.ResolvedAt = context.SubmittedAt;
            openEpisode.ResolvedBy = currentUser.UserId;
            openEpisode.ResolutionSource = ResolutionSource.AutoBelowThreshold;
            openEpisode.ResolutionReason = null;

            state.ResolvedAlerts.Add(openEpisode);

            resolved.Add(new Response.ResolvedAlert
            {
                AlertId = openEpisode.Id,
                StudentId = studentId,
                Source = nameof(ResolutionSource.AutoBelowThreshold)
            });
        }

        /// <summary>
        ///     Whether a failure is one the loop may try again, and what to report on exhaustion.
        /// </summary>
        /// <remarks>
        ///     Everything not matched here propagates unchanged — a foreign-key <c>23503</c>, a check
        ///     <c>23514</c>, an unmapped constraint, and in particular the idempotency-key
        ///     <see cref="ConflictException" />, which is a duplicate the caller supplied and will
        ///     collide identically forever. <see cref="ConcurrencyConflictException" /> deliberately does
        ///     not derive from <see cref="ConflictException" /> so this distinction is expressible at
        ///     all.
        /// </remarks>
        private static RetryableFailure? Classify(Exception exception) => exception switch
        {
            // Only StudentAttendanceSummary carries a concurrency token (F01d §3), so this is always
            // the summary race. It reaches features untranslated because its inner exception is not a
            // PostgresException, which is exactly what the SaveChangesAsync override's `when` filter
            // preserves.
            DbUpdateConcurrencyException concurrency => new RetryableFailure(
                ConcurrencyTokenFailure,
                ErrorCodes.Attendance.ConcurrentSubmission,
                "The attendance summary was updated by another submission.",
                concurrency.Entries),

            ConcurrencyConflictException conflict when RetryableConstraints.Contains(conflict.ConstraintName) =>
                new RetryableFailure(
                    conflict.ConstraintName, conflict.ErrorCode, conflict.Message, conflict.Entries),

            _ => null
        };

        /// <summary>
        ///     Undoes a discarded attempt so the next one reads the database rather than the tracker.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Three steps, and all three are needed. <b>Without the reload</b>, identity resolution
        ///         hands the next attempt the tracked instance and discards the database values, so the
        ///         stale token is never refreshed, three attempts fail identically and zero rows are
        ///         written (VC-29). <b>Without the second loop</b>, an <c>Added</c> row EF did not name
        ///         survives into the next attempt, whose re-read then produces a second instance for the
        ///         same key — a <c>23505</c> the retry treats as a race. <b>Without the first</b>, the
        ///         next attempt compares its freshly computed total against values this attempt already
        ///         wrote in memory, so a summary that genuinely changed can look unchanged and an
        ///         auto-resolution can go unreported.
        ///     </para>
        ///     <para>
        ///         <c>Remove()</c> on an <c>Added</c> entity <b>detaches</b> it rather than marking it
        ///         <c>Deleted</c>, so the audit interceptor never sees <c>EntityState.Deleted</c> and
        ///         nothing is soft-deleted. That reads like a mistake and is not;
        ///         <c>Handle_WhenAttemptIsDiscarded_DoesNotSoftDeleteTheDiscardedRows</c> is what fails
        ///         if someone "corrects" it. The reference check before each call matters for the same
        ///         reason in reverse: calling <c>Remove()</c> on an entity the loop above already
        ///         detached would begin tracking it as <c>Deleted</c>, which the interceptor would then
        ///         rewrite into a soft-delete UPDATE of a row that does not exist.
        ///     </para>
        /// </remarks>
        private async Task RecoverAsync(
            IReadOnlyList<EntityEntry> entries,
            SaveContext context,
            AttemptState state,
            CancellationToken cancellationToken)
        {
            // 1. Revert this attempt's in-memory mutations of rows that already existed. Before the
            //    reload below, so a reloaded entry ends up holding database values rather than these.
            foreach ((StudentAttendanceSummary summary, int totalAbsences, Guid schoolId) in state.UpdatedSummaries)
            {
                summary.TotalAbsences = totalAbsences;
                summary.SchoolId = schoolId;
            }

            foreach (StudentAlert alert in state.ResolvedAlerts)
            {
                alert.ResolvedAt = null;
                alert.ResolvedBy = null;
                alert.ResolutionSource = null;
                alert.ResolutionReason = null;
            }

            // 2. The entries EF named.
            HashSet<object> detached = new(ReferenceEqualityComparer.Instance);

            foreach (EntityEntry entry in entries)
            {
                // The log is created once, before the loop, and stays Added: its id is the Location
                // target and the next attempt inserts it.
                if (ReferenceEquals(entry.Entity, context.Log))
                    continue;

                if (entry.State == EntityState.Added)
                {
                    detached.Add(entry.Entity);
                    entry.State = EntityState.Detached;
                }
                else
                {
                    await entry.ReloadAsync(cancellationToken);
                }
            }

            // 3. The entities this attempt added that EF did not name.
            foreach (StudentAttendance attendance in state.AddedAttendance)
            {
                if (!detached.Contains(attendance))
                    dbContext.StudentAttendances.Remove(attendance);
            }

            foreach (StudentAttendanceSummary summary in state.AddedSummaries)
            {
                if (!detached.Contains(summary))
                    dbContext.StudentAttendanceSummaries.Remove(summary);
            }

            foreach (StudentAlert alert in state.AddedAlerts)
            {
                if (!detached.Contains(alert))
                    dbContext.StudentAlerts.Remove(alert);
            }
        }

        private static string Iso(DateOnly date) =>
            date.ToString(GetAttendanceRoster.DateFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     The <c>source</c> values a hand-constructed <see cref="Violation" /> may carry.
    /// </summary>
    /// <remarks>
    ///     <c>api/Errors/ViolationSource</c> owns the same three strings but is <c>internal</c> to
    ///     <c>api</c>, which <c>features</c> does not reference — the dependency runs the other way.
    ///     Spelled once here rather than at each call site.
    /// </remarks>
    internal static class ViolationSources
    {
        public const string Body = "body";

        public const string Path = "path";

        public const string Header = "header";
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            // Relative to the api/v1 group; writing /api/... here would double the prefix.
            //
            // Neither {schoolId} nor {date} carries a route constraint, matching F06: a :datetime or
            // :guid constraint turns a malformed value into a routing 404 with SYSTEM.NOT_FOUND,
            // indistinguishable from an unknown school — which is the outcome the string-bound date
            // exists to prevent. The route-value key must stay `date`, matching Command.Date, because
            // ViolationSource resolves the source by matching that name.
            app.MapPost("/schools/{schoolId}/attendance/{date}/submissions", async (
                    Guid schoolId,
                    string date,
                    Body? body,
                    HttpRequest httpRequest,
                    IMediator mediator,
                    CancellationToken cancellationToken) =>
                {
                    ArgumentNullException.ThrowIfNull(httpRequest);

                    // A header, not a body field (O-09): route values and the body are the resource,
                    // and the key is transport-level retry metadata. Its length bound is enforced in
                    // the handler rather than the validator, so the violation can honestly say
                    // "source": "header" — ViolationSource never infers one.
                    string? idempotencyKey = httpRequest.Headers[IdempotencyKeyHeader].FirstOrDefault();

                    Response response = await mediator.Send(
                        new Command
                        {
                            SchoolId = schoolId,
                            Date = date,
                            IdempotencyKey = idempotencyKey,
                            Entries = body?.Entries ?? []
                        },
                        cancellationToken);

                    // O-01: the log entry, whose rows F11 enumerates through
                    // StudentAttendance.SubmissionId. Because a save is a partial upsert, a later
                    // submission overwrites that column — so the target answers "which rows has this
                    // submission written that have not since been superseded".
                    return Results.Created(
                        FormattableString.Invariant($"/api/v1/attendance-submissions/{response.SubmissionId}"),
                        response);
                })
                .WithName(nameof(SaveDailyAttendance))
                .WithTags("Attendance")
                .Produces<Response>(StatusCodes.Status201Created)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }
    }
}
