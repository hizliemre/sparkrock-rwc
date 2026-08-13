using Carter;
using domain.Attendance;
using domain.Exceptions;
using domain.Security;
using features.Paging;
using FluentValidation;
using infra.persistence.sql;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace features.AttendanceSubmissions;

/// <summary>
///     <c>GET /schools/{schoolId}/attendance-submissions</c> — one school's submission log, newest
///     first, keyset paged.
/// </summary>
/// <remarks>
///     <b>Expect this to return <c>items: []</c>, and do not read that as a defect.</b> Legacy wrote
///     <c>AttendanceSubmissionLog</c> and never read it: the insert sits at <c>sp_SaveDailyAttendance</c>
///     line 120, <em>after</em> <c>CLOSE cur</c>, so a procedure aborting inside the cursor never
///     reached it — and the supplied procedure never runs at all (L-01, L-13, legacy-analysis §5). The
///     table is expected to be empty or unrepresentative in the source, F12 imports none of it
///     (<c>AttendanceSubmissionLog</c> is not <c>ILegacyEntity</c>), and on the day the new system
///     opens every school's response here is an empty page. It fills up from F07 onwards.
///     <para>
///         The consequence for this file is that its whole read contract is <b>invented</b> rather
///         than migrated. DEC-01's "faithful to intent" has nothing to be faithful to: every query
///         parameter, sort order, envelope and status code below is a new design decision, which is
///         why F11's divergence log is empty — a divergence records a difference from legacy
///         behaviour, and there is none to differ from.
///     </para>
///     <para>
///         <b>Keyset, not offset</b> (conventions §2, O-05): an append-only log grows without bound,
///         and <c>totalItems</c> would cost a <c>COUNT(*)</c> over the filtered log on every page. The
///         envelope is <see cref="KeysetResponse{TItem}" /> and carries no total by design.
///     </para>
///     <para>
///         A query slice, so it allocates no <c>EventId</c> and logs nothing (conventions §4). The
///         SubmissionLog range 1700–1799 stays entirely unallocated by F11.
///     </para>
/// </remarks>
public static partial class GetSchoolAttendanceSubmissions
{
    /// <summary>
    ///     The log query: scoped, range-filtered, ordered and projected, but <b>not</b> yet paged.
    /// </summary>
    /// <remarks>
    ///     Exposed rather than inlined for the reason <c>GetStudentAttendanceHistory.HistoryQuery</c>
    ///     gives: the assertions that matter about keyset are about the SQL this produces and the plan
    ///     Postgres chooses for it, and asserting them against a hand-written equivalent would be a
    ///     test of the test.
    ///     <para>
    ///         <b>The order of operations is the feature.</b> Filters compose, then the projection,
    ///         then the total order. Paging is the caller's <c>Take(size + 1)</c> and happens strictly
    ///         after all of it — see <see cref="QueryHandler" />.
    ///     </para>
    /// </remarks>
    internal static IQueryable<Response> LogQuery(
        IDbContext dbContext,
        Guid schoolId,
        DateOnly? from,
        DateOnly? toExclusive,
        SubmissionCursor? cursor)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        IQueryable<AttendanceSubmissionLog> logs = dbContext.AttendanceSubmissionLogs
            .AsNoTracking()
            .Where(log => log.SchoolId == schoolId);

        // By composition, one `if` each, never `(@from == null || log.AttendDate >= @from)`: a
        // disjunction against a parameter defeats an index scan for the case that has one.
        //
        // Both filter AttendDate while the order and the cursor use SubmittedAt. They are deliberately
        // different columns (spec §5): AttendDate is the question a user asks and repeats freely,
        // because a school can re-submit the same date any number of times (V-20); SubmittedAt is the
        // only column that gives an append-only log a stable total order.
        if (from.HasValue)
            logs = logs.Where(log => log.AttendDate >= from.Value);

        if (toExclusive.HasValue)
            logs = logs.Where(log => log.AttendDate < toExclusive.Value);

        if (cursor.HasValue)
        {
            DateTimeOffset cursorAt = cursor.Value.SubmittedAt;
            Guid cursorId = cursor.Value.Id;

            // O-06's predicate. `CompareTo` is the only form that compiles — C# declares no `<` on
            // Guid — and Npgsql renders it as a plain `id < @p` against a `uuid`, with no `::text`
            // cast, so the composite index stays usable. KeysetPagingTests pins both halves.
            //
            // The tie-break is not defensive. SubmittedAt is timestamptz, stored to the microsecond;
            // two submissions by one school can share one, and a single-column cursor then skips rows
            // (with <) or repeats them forever (with <=).
            logs = logs.Where(log => log.SubmittedAt < cursorAt
                                     || (log.SubmittedAt == cursorAt && log.Id.CompareTo(cursorId) < 0));
        }

        return logs
            .Select(log => new Response
            {
                Id = log.Id,
                AttendanceDate = log.AttendDate,
                SubmittedAt = log.SubmittedAt,
                RecordCount = log.RecordCount,
                SubmittedBy = new Response.SubmittedByInfo { UserId = log.SubmittedBy }
            })

            // Newest first: a submission log is read to answer "what happened recently". The Id
            // tie-break makes the order total, which is what the cursor predicate above assumes — the
            // two must agree column for column or a page boundary inside a tie drops or repeats a row.
            .OrderByDescending(item => item.SubmittedAt)
            .ThenByDescending(item => item.Id);
    }

    public sealed class Query : IRequest<KeysetResponse<Response>>
    {
        /// <summary>From the route. The scope key, and the only tenant predicate on this query.</summary>
        public required Guid SchoolId { get; init; }

        /// <summary>Inclusive lower bound on <c>AttendDate</c>. Optional and independent of <see cref="ToExclusive" />.</summary>
        public DateOnly? From { get; init; }

        /// <summary>
        ///     Exclusive upper bound on <c>AttendDate</c> (O-07).
        /// </summary>
        /// <remarks>
        ///     Named <c>toExclusive</c> and not <c>to</c>, matching F08 and conventions §2. A parameter
        ///     called <c>to</c> reads as inclusive to every client author while the predicate excludes
        ///     it, which costs one day of data at the boundary — silently, because an
        ///     off-by-one-day result set looks plausible. The name carries the definition.
        /// </remarks>
        public DateOnly? ToExclusive { get; init; }

        /// <summary>The opaque cursor from a previous page's <c>page.nextCursor</c>.</summary>
        public string? Cursor { get; init; }

        /// <summary>
        ///     Accepted only so it can be <b>rejected</b>.
        /// </summary>
        /// <remarks>
        ///     There is no page number under keyset. Binding it and failing validation is the whole
        ///     point: an unbound <c>?page=</c> is ignored, and a client that believes it is on page 7
        ///     while being served page 1 gets a plausible result set and no signal at all.
        /// </remarks>
        public int? Page { get; init; }

        public int? PageSize { get; init; }
    }

    /// <summary>One submission. The school is the route value and is deliberately not repeated.</summary>
    public sealed record Response
    {
        public required Guid Id { get; init; }

        /// <summary>The date attendance was recorded <em>for</em>, not when it was submitted.</summary>
        public required DateOnly AttendanceDate { get; init; }

        public required DateTimeOffset SubmittedAt { get; init; }

        public required int RecordCount { get; init; }

        public required SubmittedByInfo SubmittedBy { get; init; }

        /// <summary>
        ///     The submitting identity, as an object carrying a bare id.
        /// </summary>
        /// <remarks>
        ///     There is no user directory: <c>AttendanceSubmissionLog.SubmittedBy</c> is a bare
        ///     <see cref="Guid" /> and <c>ICurrentUser.DisplayName</c> exists only inside the request
        ///     that wrote the row, so F07's <c>201</c> body can carry a name and this read route
        ///     cannot. Nested rather than flattened to <c>submittedByUserId</c> precisely so a
        ///     <c>displayName</c> can be added when authentication lands, without a breaking change.
        /// </remarks>
        public sealed record SubmittedByInfo
        {
            public required Guid UserId { get; init; }
        }
    }

    internal sealed class QueryValidator : AbstractValidator<Query>
    {
        public QueryValidator()
        {
            // The same constants and the same rule builder as the offset envelope. Two envelopes, one
            // page-size contract.
            RuleFor(query => query.PageSize).ValidPageSize();

            // Attached to Page rather than to the request, so api/Errors/ViolationSource resolves the
            // violation's source from the query key the client actually sent.
            RuleFor(query => query.Page)
                .Must(page => page is null)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage("page is not accepted on a keyset-paged route. Follow page.nextCursor instead.");

            // O-06: undecodable is a 400, never a silent fallback to page 1. A client paging in a loop
            // against a cursor that resets to the beginning never terminates.
            RuleFor(query => query.Cursor)
                .Must(cursor => SubmissionCursor.TryDecode(cursor, out _))
                .WithErrorCode(ErrorCodes.Validation.InvalidCursor)
                .WithMessage("cursor is not a valid page cursor. Use the value from page.nextCursor.")
                .When(query => query.Cursor is not null);

            // Strictly before, because the range is half-open: an equal pair is an empty range, which
            // is a request that cannot have been meant. Reported rather than served as an empty page,
            // which would read as "nothing was submitted".
            RuleFor(query => query.ToExclusive)
                .Must((query, toExclusive) => query.From!.Value < toExclusive!.Value)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage("toExclusive must be after from.")
                .When(query => query.From.HasValue && query.ToExclusive.HasValue);
        }
    }

    internal sealed class QueryHandler(IDbContext dbContext, ICurrentUser currentUser)
        : IRequestHandler<Query, KeysetResponse<Response>>
    {
        public async Task<KeysetResponse<Response>> Handle(Query request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            // Scope first and alone. There is deliberately no school-exists lookup: an in-scope school
            // that has never submitted returns an empty page, which on opening day is every school.
            // A 404 there would make "has not submitted yet" indistinguishable from "no such school".
            //
            // NotFoundException takes no message, so an out-of-scope school and an unknown one are
            // byte-identical by construction rather than by call-site discipline (conventions §2).
            currentUser.EnsureAuthorized(request.SchoolId, ErrorCodes.Attendance.SubmissionNotFound);

            int size = PagingRules.ResolvePageSize(request.PageSize);

            // The validator has already rejected an undecodable cursor, so this cannot fail for a
            // request that reached the handler. Written as TryDecode anyway so the handler has no
            // second failure mode of its own.
            SubmissionCursor? cursor = SubmissionCursor.TryDecode(request.Cursor, out SubmissionCursor decoded)
                ? decoded
                : null;

            // Over-fetch by exactly one, then trim. That is what makes `hasMore` a bool rather than a
            // COUNT(*), and the sequence below is fixed: Take(size + 1) → materialise → read hasMore →
            // trim → read nextCursor from what is left. Trimming before reading hasMore loses the
            // signal; reading the cursor before trimming names a row the client was never sent.
            List<Response> rows = await LogQuery(
                    dbContext, request.SchoolId, request.From, request.ToExclusive, cursor)
                .Take(size + 1)
                .ToListAsync(cancellationToken);

            bool hasMore = rows.Count > size;

            if (hasMore)
                rows.RemoveAt(rows.Count - 1);

            return new KeysetResponse<Response>
            {
                Items = rows,
                Page = new KeysetPageInfo
                {
                    Size = size,
                    HasMore = hasMore,

                    // From the LAST row of the server-ordered page, never from an aggregate over it
                    // (plan R-2). `rows.Max(r => r.Id)` would order Guids in C#, where Guid.CompareTo
                    // compares Data1 as a signed int while Postgres compares all sixteen bytes
                    // big-endian — and it would look like tidying.
                    NextCursor = hasMore
                        ? new SubmissionCursor(rows[^1].SubmittedAt, rows[^1].Id).Encode()
                        : null
                }
            };
        }
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            // Relative to the api/v1 group; writing /api/... here would double the prefix.
            app.MapGet("/schools/{schoolId:guid}/attendance-submissions", async (
                    Guid schoolId,
                    DateOnly? from,
                    DateOnly? toExclusive,
                    string? cursor,
                    int? page,
                    int? pageSize,
                    IMediator mediator,
                    CancellationToken cancellationToken) =>
                {
                    // Every parameter name here is also a Query property name. The kernel infers a
                    // violation's `source` by matching the property path against the request's query
                    // keys, so a rename on either side silently degrades "source": "query" into
                    // whatever the fallback answers.
                    //
                    // `page` is bound only so the validator can refuse it. Dropping it from this list
                    // would make ?page= silently ignored again.
                    KeysetResponse<Response> submissions = await mediator.Send(
                        new Query
                        {
                            SchoolId = schoolId,
                            From = from,
                            ToExclusive = toExclusive,
                            Cursor = cursor,
                            Page = page,
                            PageSize = pageSize
                        },
                        cancellationToken);

                    return Results.Ok(submissions);
                })
                .WithName(nameof(GetSchoolAttendanceSubmissions))
                .WithTags("AttendanceSubmissions")
                .Produces<KeysetResponse<Response>>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
    }
}
