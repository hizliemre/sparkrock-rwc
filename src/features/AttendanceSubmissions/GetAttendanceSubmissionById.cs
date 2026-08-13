using System.Text.Json.Serialization;
using Carter;
using domain.Attendance;
using domain.Exceptions;
using domain.Security;
using infra.persistence.sql;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace features.AttendanceSubmissions;

/// <summary>
///     <c>GET /attendance-submissions/{submissionId}</c> — one submission's header and the attendance
///     rows it wrote. The target of F07's <c>Location</c> (conventions §1).
/// </summary>
/// <remarks>
///     <b>What "the rows it wrote" means, precisely.</b> Attendance is a partial upsert (D-08, V-20):
///     when a later submission updates an existing row, that row's <c>SubmissionId</c> is
///     <em>overwritten</em>. So this route returns the rows carrying this submission's id — the ones
///     it wrote <b>that have not since been superseded</b>. F11 does not hide the gap: it returns
///     <see cref="Response.SupersededCount" />, so the difference between <c>recordCount</c> and
///     <c>entries.length</c> is a number in the payload rather than a discrepancy the client has to
///     notice for itself. Non-zero is normal, not an error.
///     <para>
///         O-01 warned that without <c>StudentAttendance.SubmissionId</c> this route could answer with
///         a header alone. F01d added the column, so it can do better than that — but not perfectly,
///         and <c>supersededCount</c> is the honest form of "not perfectly". The full trail would need
///         the <c>AttendanceSubmissionEntry</c> join table F01d considered and rejected (F01d §4.1):
///         it doubles the write volume of the hottest path, needs its own retention answer, and
///         nothing reads it.
///     </para>
///     <para>
///         <c>entries</c> is deliberately <b>not</b> paged. F07's batch cap bounds a submission at 500
///         entries, and paging them would make the <c>Location</c> target a two-request resource while
///         <c>supersededCount</c> would still have to be computed separately. The bound is F07's; if
///         it ever rises this becomes a real page (plan R-4).
///     </para>
///     <para>
///         A query slice: no <c>EventId</c>, no logging (conventions §4).
///     </para>
/// </remarks>
public static partial class GetAttendanceSubmissionById
{
    public sealed class Query : IRequest<Response>
    {
        public required Guid SubmissionId { get; init; }
    }

    public sealed record Response
    {
        public required Guid Id { get; init; }

        /// <summary>
        ///     Returned here, unlike on the list.
        /// </summary>
        /// <remarks>
        ///     This route is not school-nested, so the school is not otherwise knowable from the
        ///     request — and the caller has already proven authorisation for it to get this far.
        /// </remarks>
        public required Guid SchoolId { get; init; }

        public required DateOnly AttendanceDate { get; init; }

        public required DateTimeOffset SubmittedAt { get; init; }

        /// <summary>What the submission wrote, as recorded when it was written.</summary>
        public required int RecordCount { get; init; }

        /// <summary>
        ///     <see cref="RecordCount" /> minus <see cref="Entries" />' length: rows this submission
        ///     wrote that no longer carry its id.
        /// </summary>
        /// <remarks>
        ///     Almost always a later submission overwriting the row (V-20's partial upsert). A
        ///     <b>soft-deleted</b> attendance row also reduces the count — it is hidden by the
        ///     reflective query filter (DEC-20) and is indistinguishable from a supersede here. Stated
        ///     rather than hidden: the two have the same meaning for this route, which is "this row is
        ///     no longer what this submission left behind".
        ///     <para>
        ///         Not clamped at zero. Under F07 it cannot go negative, and a negative value would be
        ///         a real defect worth seeing rather than one worth flooring away.
        ///     </para>
        /// </remarks>
        public required int SupersededCount { get; init; }

        public required SubmittedByInfo SubmittedBy { get; init; }

        /// <summary>Ordered <c>lastName, firstName, studentId</c> — a total order (VC-27).</summary>
        public required IReadOnlyList<EntryInfo> Entries { get; init; }

        /// <inheritdoc cref="GetSchoolAttendanceSubmissions.Response.SubmittedByInfo" />
        public sealed record SubmittedByInfo
        {
            public required Guid UserId { get; init; }
        }

        /// <summary>
        ///     One attendance row.
        /// </summary>
        /// <remarks>
        ///     <b>No <c>notes</c>.</b> O-17 records that <c>Notes</c> is required by the roster and
        ///     history read paths and forbidden by conventions §2, and assigns the decision to F06 and
        ///     F08. F11 neither needs it nor pre-empts it: the submission log answers "what was
        ///     submitted", and the free-text field is the one that carries safeguarding and health
        ///     detail.
        ///     <para>
        ///         No <c>termId</c> and no <c>schoolYear</c> either — both are properties of the
        ///         attendance row rather than of the submission, and F08 is the history route.
        ///     </para>
        ///     <para>
        ///         The code fields come from the row's snapshot columns and the code table is never
        ///         joined, which is what makes a since-deactivated code still render (DEC-19, V-23).
        ///     </para>
        /// </remarks>
        public sealed record EntryInfo
        {
            public required Guid StudentId { get; init; }

            public required string StudentFirstName { get; init; }

            public required string StudentLastName { get; init; }

            public required string AttendCode { get; init; }

            public required string AttendCodeDescription { get; init; }

            public required bool IsAbsent { get; init; }

            public required bool IsExcused { get; init; }

            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public int? MinutesLate { get; init; }
        }
    }

    /// <summary>
    ///     The entries query: rows carrying the submission's id, joined to their students.
    /// </summary>
    /// <remarks>
    ///     Exposed so the integration tier can assert on the SQL and the plan this produces rather than
    ///     on a hand-written equivalent.
    ///     <para>
    ///         An explicit join, because F01d declares no navigation properties. Inner, because the
    ///         foreign key makes an attendance row without a student unwritable — and an absent one
    ///         would be a data defect this endpoint should report as a missing row rather than as a
    ///         500 on a null dereference.
    ///     </para>
    ///     <para>
    ///         No <c>WhereAuthorized</c> here, and the absence is deliberate: the caller's access to
    ///         this submission has already been established by the scoped lookup in
    ///         <see cref="QueryHandler" />, and re-applying tenant scope to the rows would silently
    ///         drop entries rather than refuse the request — a partial answer presented as a complete
    ///         one, which is the worse failure.
    ///     </para>
    /// </remarks>
    internal static IQueryable<Response.EntryInfo> EntriesQuery(IDbContext dbContext, Guid submissionId)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        return from attendance in dbContext.StudentAttendances.AsNoTracking()
            join student in dbContext.Students.AsNoTracking() on attendance.StudentId equals student.Id
            where attendance.SubmissionId == submissionId

            // Total, so the response is stable across reads and under the global SplitQuery setting
            // (VC-27) — and it is also the order a roster is read in.
            orderby student.LastName, student.FirstName, attendance.StudentId
            select new Response.EntryInfo
            {
                StudentId = attendance.StudentId,
                StudentFirstName = student.FirstName,
                StudentLastName = student.LastName,
                AttendCode = attendance.AttendCode,
                AttendCodeDescription = attendance.AttendCodeDescription,
                IsAbsent = attendance.IsAbsent,
                IsExcused = attendance.IsExcused,
                MinutesLate = attendance.MinutesLate
            };
    }

    internal sealed class QueryHandler(IDbContext dbContext, ICurrentUser currentUser)
        : IRequestHandler<Query, Response>
    {
        public async Task<Response> Handle(Query request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            // Tenant scope is part of the lookup (DEC-15), not a check applied to the row afterwards.
            // Spec §6 numbers "absent → 404" and "out of scope → 404, identical payload" as two steps;
            // they throw the same exception with the same code and no message, so folding them into
            // one predicate makes conventions §2's existence oracle closed *by construction* rather
            // than by two call sites agreeing to stay in step.
            //
            // WhereAuthorized rather than EnsureAuthorized because this route has no {schoolId}
            // segment: there is no path school to authorise against before the read, so the subject of
            // the query has to be narrowed by the identity's scope instead.
            AttendanceSubmissionLog? log = await dbContext.AttendanceSubmissionLogs
                .AsNoTracking()
                .WhereAuthorized(currentUser)
                .FirstOrDefaultAsync(candidate => candidate.Id == request.SubmissionId, cancellationToken);

            if (log is null)
                throw new NotFoundException(ErrorCodes.Attendance.SubmissionNotFound);

            List<Response.EntryInfo> entries =
                await EntriesQuery(dbContext, request.SubmissionId).ToListAsync(cancellationToken);

            return new Response
            {
                Id = log.Id,
                SchoolId = log.SchoolId,
                AttendanceDate = log.AttendDate,
                SubmittedAt = log.SubmittedAt,
                RecordCount = log.RecordCount,
                SupersededCount = log.RecordCount - entries.Count,
                SubmittedBy = new Response.SubmittedByInfo { UserId = log.SubmittedBy },
                Entries = entries
            };
        }
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            // Relative to the api/v1 group, so this resolves at /api/v1/attendance-submissions/{id} —
            // byte-identical to the Location F07 emits. A route that does not resolve its own
            // Location target is the defect this route exists to prevent.
            app.MapGet("/attendance-submissions/{submissionId:guid}", async (
                    Guid submissionId,
                    IMediator mediator,
                    CancellationToken cancellationToken) =>
                {
                    Response submission = await mediator.Send(
                        new Query { SubmissionId = submissionId },
                        cancellationToken);

                    return Results.Ok(submission);
                })
                .WithName(nameof(GetAttendanceSubmissionById))
                .WithTags("AttendanceSubmissions")
                .Produces<Response>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
    }
}
