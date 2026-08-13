using System.Globalization;
using System.Text.Json.Serialization;
using Carter;
using domain.Attendance;
using domain.Exceptions;
using domain.Security;
using domain.Students;
using features.Paging;
using FluentValidation;
using infra.persistence.sql;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace features.Attendance;

public static partial class GetAttendanceRoster
{
    /// <summary>
    ///     The one accepted wire format for <c>{date}</c> (conventions §2).
    /// </summary>
    public const string DateFormat = "yyyy-MM-dd";

    /// <summary>No attendance row exists for this student on this date. <c>attendance</c> is absent.</summary>
    public const string StatusNotRecorded = "notRecorded";

    /// <summary>A row exists and belongs to the addressed school. <c>attendance</c> is present.</summary>
    public const string StatusRecorded = "recorded";

    /// <summary>
    ///     A row exists for this student and date at <b>another</b> school. <c>attendance</c> is absent.
    /// </summary>
    /// <remarks>
    ///     The state falls out of joining on <c>(StudentId, AttendDate)</c> without <c>SchoolId</c>,
    ///     which V-06 forces: legacy's dedup key is one record per student per day, globally. Adding
    ///     <c>SchoolId</c> to the join would report <c>notRecorded</c> for a day that cannot be recorded
    ///     again, and the clerk's submission would collide with a row they were never shown.
    ///     <para>
    ///         The fact is reported and the payload withheld — the code, minutes and note were recorded
    ///         by a school this caller has no access to (DEC-15). Same reasoning as DEC-16's refusal to
    ///         return <c>thresholdSourceSchoolId</c>.
    ///     </para>
    /// </remarks>
    public const string StatusRecordedElsewhere = "recordedElsewhere";

    /// <summary>
    ///     Parses a <c>{date}</c> route value, and is the only place the format is applied.
    /// </summary>
    /// <remarks>
    ///     <c>TryParseExact</c> with a single pattern rather than <c>DateOnly.TryParse</c>, which
    ///     accepts <c>09/14/2026</c> under the invariant culture — the format conventions §2 rejects.
    ///     Shared by the validator and the handler so the two cannot drift.
    /// </remarks>
    internal static bool TryParseDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    public sealed class Query : IRequest<PagedResponse<Response>>
    {
        /// <summary>From the route. The scope key, and the only tenant predicate on this query.</summary>
        public required Guid SchoolId { get; init; }

        /// <summary>
        ///     From the route, as a <c>string</c>.
        /// </summary>
        /// <remarks>
        ///     Bound as a string and validated rather than as a <see cref="DateOnly" />, so
        ///     <c>2026-13-01</c> is a 400 <c>VALIDATION.FAILED</c> instead of a routing 404 that would
        ///     be indistinguishable from an unknown school (spec §6).
        ///     <para>
        ///         <b>The property name is load-bearing.</b> <c>api/Errors/ViolationSource</c> infers a
        ///         violation's <c>source</c> by matching the camel-cased root of the property path
        ///         against the request's route values, then its query keys. <c>Date</c> → <c>date</c> →
        ///         the route value → <c>"path"</c>. Renaming it to <c>AttendDate</c> matches neither,
        ///         and a malformed date would be reported as a query-parameter failure.
        ///     </para>
        /// </remarks>
        public required string Date { get; init; }

        /// <summary>
        ///     Optional exact match on <c>Student.Grade</c>, trimmed. Absent <b>or blank</b> means all
        ///     grades.
        /// </summary>
        /// <remarks>
        ///     <b>V-24.</b> L-15 established that <c>cboGrade.Clear()</c> ran immediately before the only
        ///     read of <c>cboGrade.Text</c>, so legacy passed <c>''</c> to
        ///     <c>sp_GetStudentsForAttendance</c> on every call — the filter never filtered, and "all
        ///     grades" is the only behaviour anyone ever observed. That inverts D-06's earlier reading
        ///     that <c>@Grade</c> is required.
        ///     <para>
        ///         The rule is <b>identical to F05's</b> <c>GetStudents.Query.Grade</c> and must stay
        ///         that way: absent or blank is all grades including null grades; otherwise a trimmed,
        ///         exact, ordinal match that excludes null grades. The two features filter different
        ///         queries, so there is no shared function to extract without moving the predicate into
        ///         <c>domain</c>. Parity is pinned instead by
        ///         <c>GetAttendanceRosterGradeFilterParityTests</c>, which drives both handlers over one
        ///         seeded school and asserts they select the same students.
        ///     </para>
        /// </remarks>
        public string? Grade { get; init; }

        public int? Page { get; init; }

        public int? PageSize { get; init; }
    }

    /// <summary>
    ///     One roster row: a student, and what has already been recorded for them on the date.
    /// </summary>
    public sealed record Response
    {
        public required Guid StudentId { get; init; }

        public required string LastName { get; init; }

        public required string FirstName { get; init; }

        /// <summary>Omitted rather than serialised as <c>null</c> (conventions §2).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Grade { get; init; }

        /// <summary>
        ///     <see cref="StatusNotRecorded" />, <see cref="StatusRecorded" /> or
        ///     <see cref="StatusRecordedElsewhere" />.
        /// </summary>
        public required string Status { get; init; }

        /// <summary>
        ///     Present only when <see cref="Status" /> is <see cref="StatusRecorded" />.
        /// </summary>
        /// <remarks>
        ///     The three states are distinguished at the object level, not by a null probe on one
        ///     field: a student recorded with no note is <c>recorded</c> with an
        ///     <see cref="AttendanceDetail" /> whose <c>notes</c> is omitted, and a student not yet
        ///     recorded has no <c>attendance</c> object at all.
        /// </remarks>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AttendanceDetail? Attendance { get; init; }

        /// <summary>
        ///     The attendance already recorded for this student on this date, by this school.
        /// </summary>
        /// <remarks>
        ///     Every code field is the snapshot stored on <c>student_attendances</c> (D-02). Nothing
        ///     here is resolved from <c>attendance_codes</c> at read time, which is why
        ///     <c>StudentAttendance</c> carries no navigation to it.
        /// </remarks>
        public sealed record AttendanceDetail
        {
            public required Guid AttendanceId { get; init; }

            public required string AttendCode { get; init; }

            public required string AttendCodeDescription { get; init; }

            public required bool IsAbsent { get; init; }

            public required bool IsExcused { get; init; }

            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public int? MinutesLate { get; init; }

            /// <summary>
            ///     Free text, returned as stored.
            /// </summary>
            /// <remarks>
            ///     O-17's second branch, taken by F06 and F08 together: conventions §2's ban on
            ///     <c>Notes</c> is scoped to error messages, log templates and telemetry. A clerk
            ///     correcting one student's record must see the note attached to it, and F07's partial
            ///     upsert would blank every note the clerk did not retype without it.
            /// </remarks>
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? Notes { get; init; }

            /// <summary>Null when no term covered the date — preserved from legacy, not rejected.</summary>
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public Guid? TermId { get; init; }

            /// <summary><c>ModifiedAt ?? CreatedAt</c> — V-21's global projection rule.</summary>
            public required DateTimeOffset LastUpdatedAt { get; init; }
        }
    }

    internal sealed class QueryValidator : AbstractValidator<Query>
    {
        public QueryValidator()
        {
            RuleFor(query => query.Page).ValidPage();
            RuleFor(query => query.PageSize).ValidPageSize();

            // No bound on the date, upper or lower. DEC-12 bounds *submitted* dates (V-25) because an
            // unbounded write puts attendance into an arbitrary school year; reading a future roster
            // returns every student as notRecorded and discloses nothing.
            RuleFor(query => query.Date)
                .Must(date => TryParseDate(date, out _))
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage(FormattableString.Invariant($"Date must be an ISO 8601 date, {DateFormat}."));

            // Deliberately no rule on Grade. Spec §7 is the complete documented failure set for this
            // route and has no grade row: a blank grade is not a filter (V-24) and an over-long one
            // matches nothing, so neither is a request the caller got wrong.
        }
    }

    internal sealed class QueryHandler(IDbContext dbContext, ICurrentUser currentUser)
        : IRequestHandler<Query, PagedResponse<Response>>
    {
        public async Task<PagedResponse<Response>> Handle(Query request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            // Scope first, then existence, so an out-of-scope school and an unknown one produce
            // byte-identical 404s. NotFoundException takes no message parameter, which makes that true
            // by construction rather than by call-site discipline (conventions §2).
            currentUser.EnsureAuthorized(request.SchoolId, ErrorCodes.School.NotFound);

            bool schoolExists = await dbContext.Schools
                .AsNoTracking()
                .AnyAsync(school => school.Id == request.SchoolId, cancellationToken);

            // Not filtered on the school's own IsActive: GET on an inactive resource is a 200
            // (conventions §2). Refusing to *submit* to an inactive school is V-14 and is F07's.
            if (!schoolExists)
                throw new NotFoundException(ErrorCodes.School.NotFound);

            if (!TryParseDate(request.Date, out DateOnly attendDate))
            {
                // Unreachable through the pipeline: ValidationBehavior runs QueryValidator first and
                // the same TryParseDate is the rule. Stated rather than swallowed, because a handler
                // that silently substituted a date would serve the wrong day's roster.
                throw new InvalidOperationException(
                    FormattableString.Invariant($"Date must be validated as {DateFormat} before the handler runs."));
            }

            // WhereAuthorized is deliberately not added on top: the query is already keyed on a
            // schoolId EnsureAuthorized approved, and two scoping mechanisms on one query means
            // neither is obviously the one doing the work.
            IQueryable<Student> students = dbContext.Students
                .AsNoTracking()
                .Where(student => student.SchoolId == request.SchoolId && student.IsActive);

            if (!string.IsNullOrWhiteSpace(request.Grade))
            {
                // Trimmed once, in memory, so the predicate is a plain column comparison rather than a
                // trim() applied per row. Exact and ordinal: no case folding, no prefix match. Students
                // with a null Grade are therefore excluded whenever the filter is present.
                //
                // This is character-for-character F05's rule (GetStudents.QueryHandler). The two
                // features filter different queries and share no function; parity is pinned by
                // GetAttendanceRosterGradeFilterParityTests rather than by structure.
                string grade = request.Grade.Trim();

                students = students.Where(student => student.Grade == grade);
            }

            // The right side of the join, keyed on the date only — the student key is the join key
            // below. SchoolId is deliberately absent from both: V-06 keeps legacy's dedup key at one
            // record per student per day, globally, so a row written by the student's previous school
            // must be visible to the join or the roster would report notRecorded for a day that cannot
            // be recorded again. The soft-delete filter applies here automatically (VC-13), so a
            // withdrawn correction reads as notRecorded.
            IQueryable<StudentAttendance> attendanceOnDate = dbContext.StudentAttendances
                .AsNoTracking()
                .Where(attendance => attendance.AttendDate == attendDate);

            Guid schoolId = request.SchoolId;

            // A left join, and it must stay one: an inner join would drop every student with nothing
            // recorded, which is most of the roster on the screen this endpoint exists to draw. The
            // unique index on (student_id, attend_date) is what stops the join multiplying rows, so
            // the count is over students.
            IQueryable<Response> roster = students
                .GroupJoin(
                    attendanceOnDate,
                    student => student.Id,
                    attendance => attendance.StudentId,
                    (student, attendances) => new { Student = student, Attendances = attendances })
                .SelectMany(
                    pair => pair.Attendances.DefaultIfEmpty(),
                    (pair, attendance) => new Response
                    {
                        StudentId = pair.Student.Id,
                        LastName = pair.Student.LastName,
                        FirstName = pair.Student.FirstName,
                        Grade = pair.Student.Grade,

                        // The school comparison belongs here, in the projection, and never in the join.
                        Status = attendance == null
                            ? StatusNotRecorded
                            : attendance.SchoolId == schoolId
                                ? StatusRecorded
                                : StatusRecordedElsewhere,

                        // Withheld structurally rather than nulled field by field: recordedElsewhere
                        // carries no payload at all, so there is nothing for a future field to leak
                        // through (DEC-15).
                        Attendance = attendance != null && attendance.SchoolId == schoolId
                            ? new Response.AttendanceDetail
                            {
                                AttendanceId = attendance.Id,

                                // The four snapshot columns, read from student_attendances and never
                                // recomputed from attendance_codes (D-02). StudentAttendance carries no
                                // navigation to the code, so the wrong version does not compile.
                                AttendCode = attendance.AttendCode,
                                AttendCodeDescription = attendance.AttendCodeDescription,
                                IsAbsent = attendance.IsAbsent,
                                IsExcused = attendance.IsExcused,

                                MinutesLate = attendance.MinutesLate,
                                Notes = attendance.Notes,
                                TermId = attendance.TermId,
                                LastUpdatedAt = attendance.ModifiedAt ?? attendance.CreatedAt
                            }
                            : null
                    })

                // The legacy grid's order, and total: a non-total order under the global SplitQuery
                // setting can repeat a row on one page and drop another (VC-27).
                .OrderBy(row => row.LastName)
                .ThenBy(row => row.FirstName)
                .ThenBy(row => row.StudentId);

            // Projected before paging, so Skip/Take reads the projection rather than whole rows.
            return await roster.ToPagedResponseAsync(request.Page, request.PageSize, cancellationToken);
        }
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            // Relative to the api/v1 group; writing /api/... here would double the prefix.
            //
            // {date} carries no route constraint: a :datetime constraint turns a malformed date into a
            // routing 404 with SYSTEM.NOT_FOUND, indistinguishable from an unknown school — the outcome
            // spec §6 exists to prevent. The route-value key must stay `date`, matching Query.Date.
            //
            // {schoolId} carries no :guid constraint either, and for the same family of reasons: the
            // constraint would make a non-Guid a routing 404, and spec §7 documents it as a 400
            // SYSTEM.MALFORMED_REQUEST from the framework's binding failure. F02 and F05 spell their
            // school segment the same way.
            app.MapGet("/schools/{schoolId}/attendance/{date}", async (
                    Guid schoolId,
                    string date,
                    string? grade,
                    int? page,
                    int? pageSize,
                    IMediator mediator,
                    CancellationToken cancellationToken) =>
                {
                    PagedResponse<Response> roster = await mediator.Send(
                        new Query
                        {
                            SchoolId = schoolId,
                            Date = date,
                            Grade = grade,
                            Page = page,
                            PageSize = pageSize
                        },
                        cancellationToken);

                    return Results.Ok(roster);
                })
                .WithName(nameof(GetAttendanceRoster))
                .WithTags("Attendance")
                .Produces<PagedResponse<Response>>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
    }
}
