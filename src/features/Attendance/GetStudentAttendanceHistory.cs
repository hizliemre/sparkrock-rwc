using System.Text.Json.Serialization;
using Carter;
using domain.Exceptions;
using domain.Security;
using domain.ValueObjects;
using features.Paging;
using FluentValidation;
using infra.persistence.sql;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace features.Attendance;

/// <summary>
///     <c>GET /students/{studentId}/attendance</c> — one student's row-level attendance history over a
///     school year or an explicit date range, paged.
/// </summary>
public static partial class GetStudentAttendanceHistory
{
    /// <summary>A row recorded by the school the student is enrolled at today.</summary>
    internal const string CurrentSchool = "currentSchool";

    /// <summary>
    ///     A row recorded by a school the student has since left — and which the caller may have no
    ///     access to. The discriminator is returned; the other school's identity never is (spec §4.3).
    /// </summary>
    internal const string OtherSchool = "otherSchool";

    public sealed class Query : IRequest<PagedResponse<Response>>
    {
        public required Guid StudentId { get; init; }

        public int? SchoolYear { get; init; }

        public DateOnly? From { get; init; }

        public DateOnly? ToExclusive { get; init; }

        public int? Page { get; init; }

        public int? PageSize { get; init; }
    }

    public sealed record Response
    {
        public required Guid AttendanceId { get; init; }

        public required DateOnly AttendDate { get; init; }

        public required string AttendCode { get; init; }

        public required string AttendCodeDescription { get; init; }

        public required bool IsAbsent { get; init; }

        public required bool IsExcused { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MinutesLate { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Notes { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Guid? TermId { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TermName { get; init; }

        public required string Origin { get; init; }

        public required DateTimeOffset LastUpdatedAt { get; init; }
    }

    /// <summary>
    ///     Spec §1's three accepted range forms, and nothing else.
    /// </summary>
    /// <remarks>
    ///     Every rule is attached to a <b>named</b> property, never to <c>RuleFor(query =&gt; query)</c>.
    ///     The kernel infers a violation's <c>source</c> from the root segment of its property path
    ///     (<c>api/Errors/ViolationSource</c>); an empty path falls through to a last step that answers
    ///     <c>query</c> only because a GET usually carries no <c>Content-Type</c>, so a client that
    ///     sends one would flip the same violation to <c>body</c> (spec §7).
    ///     <para>
    ///         Each rule is attached to a property the request actually carried, so the inference
    ///         resolves on the query key rather than on the fallback: the conflict rules to
    ///         <c>SchoolYear</c>, which is the parameter whose presence creates the conflict, and the
    ///         open-ended-range rules to whichever date <em>was</em> supplied.
    ///     </para>
    /// </remarks>
    internal sealed class QueryValidator : AbstractValidator<Query>
    {
        public QueryValidator()
        {
            RuleFor(query => query.Page).ValidPage();
            RuleFor(query => query.PageSize).ValidPageSize();

            // The difference between a 400 and an incident. SchoolYear.FromStartYear throws
            // ArgumentOutOfRangeException, which reaches no IExceptionHandler and surfaces as a 500
            // SYSTEM.UNEXPECTED on the graded-minimum endpoint (spec §1).
            RuleFor(query => query.SchoolYear)
                .InclusiveBetween(SchoolYear.MinStartYear, SchoolYear.MaxStartYear)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage(FormattableString.Invariant(
                    $"schoolYear must be between {SchoolYear.MinStartYear} and {SchoolYear.MaxStartYear}."))
                .When(query => query.SchoolYear.HasValue);

            // Two range specifications, no defined precedence — so neither is guessed.
            RuleFor(query => query.SchoolYear)
                .Must((query, _) => query.From is null && query.ToExclusive is null)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage("schoolYear must not be combined with from or toExclusive.")
                .When(query => query.SchoolYear.HasValue);

            // An open-ended range is an unbounded read dressed as a filter. Conditioned on schoolYear
            // being absent so the conflict above is reported once rather than alongside this.
            RuleFor(query => query.From)
                .Must((query, _) => query.ToExclusive.HasValue)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage("from requires toExclusive.")
                .When(query => query.SchoolYear is null && query.From.HasValue);

            RuleFor(query => query.ToExclusive)
                .Must((query, _) => query.From.HasValue)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage("toExclusive requires from.")
                .When(query => query.SchoolYear is null && query.ToExclusive.HasValue);

            // Strictly before, because the range is half-open: an equal pair is an empty range, which
            // is a request that cannot have been meant.
            RuleFor(query => query.ToExclusive)
                .Must((query, toExclusive) => query.From!.Value < toExclusive!.Value)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage("toExclusive must be after from.")
                .When(query => query.From.HasValue && query.ToExclusive.HasValue);
        }
    }

    /// <summary>
    ///     The pure half of the range decision, so it is testable without a database.
    /// </summary>
    /// <remarks>
    ///     The validator has already rejected every combination that is not one of spec §1's three, so
    ///     this only has to pick between them. It resolves to a <b>date range</b> in every case —
    ///     never to a school-year value that a predicate would then have to recompute per row (V-12).
    /// </remarks>
    internal static (DateOnly From, DateOnly ToExclusive) ResolveRange(
        int? schoolYear,
        DateOnly? from,
        DateOnly? toExclusive,
        DateOnly schoolLocalToday)
    {
        if (schoolYear.HasValue)
            return SchoolYear.FromStartYear(schoolYear.Value).ToDateRange();

        if (from.HasValue && toExclusive.HasValue)
            return (from.Value, toExclusive.Value);

        return SchoolYear.FromLocalDate(schoolLocalToday).ToDateRange();
    }

    /// <summary>
    ///     "Today" in the school's own zone (DEC-12).
    /// </summary>
    /// <remarks>
    ///     <c>DateTimeOffset.UtcNow</c> is banned in <c>features</c> (conventions §7) and would also be
    ///     wrong: it rolls the school year at midnight UTC, which is mid-afternoon in
    ///     <c>America/Vancouver</c> — so a request on the evening of 31 August would resolve to the
    ///     <em>next</em> school year.
    ///     <para>
    ///         <see cref="TimeZoneInfo.FindSystemTimeZoneById" /> throws on an unresolvable id and is
    ///         deliberately not caught. F02 validates <c>TimeZoneId</c> at write time; a stored value
    ///         that no longer resolves is a data defect, and a 500 is the correct answer to one.
    ///     </para>
    /// </remarks>
    private static DateOnly SchoolLocalToday(TimeProvider timeProvider, string timeZoneId)
    {
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), zone).DateTime);
    }

    /// <summary>The student's current school: the authorisation subject and the source of the zone.</summary>
    private sealed record Subject
    {
        public required Guid SchoolId { get; init; }

        public required string TimeZoneId { get; init; }
    }

    internal sealed class QueryHandler(
        IDbContext dbContext,
        ICurrentUser currentUser,
        TimeProvider timeProvider) : IRequestHandler<Query, PagedResponse<Response>>
    {
        public async Task<PagedResponse<Response>> Handle(Query request, CancellationToken cancellationToken)
        {
            // The student is loaded UNSCOPED, and the scope check is made against the row that comes
            // back (spec §4.1). The route has no {schoolId} segment, so there is no path school to
            // authorise against before the read — the subject of the query has to be resolved first.
            //
            // The school is joined in the same round trip because it is the same row's foreign key and
            // is needed for the timezone. The join is inner: the foreign key makes a student without a
            // school unwritable, and an absent one would be a data defect that this endpoint reports
            // as a 404 rather than as a 500 on a null dereference.
            Subject? subject = await (
                    from student in dbContext.Students.AsNoTracking()
                    join school in dbContext.Schools.AsNoTracking() on student.SchoolId equals school.Id
                    where student.Id == request.StudentId
                    select new Subject { SchoolId = student.SchoolId, TimeZoneId = school.TimeZoneId })
                .FirstOrDefaultAsync(cancellationToken);

            if (subject is null)
                throw new NotFoundException(ErrorCodes.Student.NotFound);

            // DEC-16: access follows Student.SchoolId, so a former school loses access at transfer.
            // 404 with a payload identical to genuine not-found — NotFoundException takes no message,
            // so the two are identical by construction rather than by discipline (conventions §2).
            currentUser.EnsureAuthorized(subject.SchoolId, ErrorCodes.Student.NotFound);

            // Resolved BEFORE the query, into two plain DateOnly values. This is the whole of V-12:
            // the predicate below compares a column against parameters, so it is a range scan on
            // (student_id, attend_date) rather than a computed year evaluated per row (L-09).
            (DateOnly rangeFrom, DateOnly rangeToExclusive) = ResolveRange(
                request.SchoolYear,
                request.From,
                request.ToExclusive,
                SchoolLocalToday(timeProvider, subject.TimeZoneId));

            // Projected and filtered in the database, paged last. Materialising first would read the
            // student's entire lifetime on every page request and count the wrong set (spec §2(c)).
            return await HistoryQuery(
                    dbContext, request.StudentId, subject.SchoolId, rangeFrom, rangeToExclusive)
                .ToPagedResponseAsync(request.Page, request.PageSize, cancellationToken);
        }
    }

    /// <summary>
    ///     The history query itself: filtered, ordered and projected, but not yet paged.
    /// </summary>
    /// <remarks>
    ///     Extracted from <see cref="QueryHandler" /> so V-12's <c>EXPLAIN</c> assertions at the
    ///     integration tier can call <c>ToQueryString()</c> on <b>this</b> query rather than on a
    ///     hand-written equivalent. A hand-written equivalent is a test of the test: it can be sargable
    ///     while the shipped query is not, and the divergence log would then record the wrong thing.
    /// </remarks>
    internal static IQueryable<Response> HistoryQuery(
        IDbContext dbContext,
        Guid studentId,
        Guid currentSchoolId,
        DateOnly rangeFrom,
        DateOnly rangeToExclusive)
    {
        // NOT .WhereAuthorized(currentUser), and its absence is the feature (spec §4.2).
        // StudentAttendance implements ISchoolScoped, so the call compiles and reads as correct; it
        // would silently truncate a transferred student's year at the transfer boundary, which is
        // exactly the part of the record a safeguarding question is about. The authorisation that
        // applies is the membership check in the handler, and this query is keyed by its subject.
        //
        // The range is half-open and is a comparison of a column against two parameters. Never
        // SchoolYear.FromLocalDate(a.AttendDate) == year — a static call over a column, and the literal
        // transliteration of get:30-34 — and never a member of a converted value: both compile, both
        // throw at translation on Npgsql, and the second passes on EF InMemory (VC-31, spec §2).
        IQueryable<domain.Attendance.StudentAttendance> attendances = dbContext.StudentAttendances
            .AsNoTracking()
            .Where(attendance => attendance.StudentId == studentId)
            .Where(attendance => attendance.AttendDate >= rangeFrom
                                 && attendance.AttendDate < rangeToExclusive);

        // The term name is an explicit LEFT JOIN because F01d declares no navigation properties.
        // There is deliberately no join to attendance_codes: the code's meaning is read from the
        // snapshot columns, which is what makes a deactivated code still render (DEC-19, V-23).
        return from attendance in attendances
            join term in dbContext.SchoolTerms.AsNoTracking()
                on attendance.TermId equals (Guid?)term.Id into matchedTerms
            from term in matchedTerms.DefaultIfEmpty()

            // Legacy ordered by AttendDate descending alone; Id makes it total, because a non-total
            // order under the global SplitQuery setting can repeat a row across pages (VC-27).
            // (StudentId, AttendDate) is unique, so the tiebreak never actually fires.
            orderby attendance.AttendDate descending, attendance.Id
            select new Response
            {
                AttendanceId = attendance.Id,
                AttendDate = attendance.AttendDate,
                AttendCode = attendance.AttendCode,
                AttendCodeDescription = attendance.AttendCodeDescription,
                IsAbsent = attendance.IsAbsent,
                IsExcused = attendance.IsExcused,
                MinutesLate = attendance.MinutesLate,
                Notes = attendance.Notes,
                TermId = attendance.TermId,
                TermName = term == null ? null : term.Name,

                // The fact that a row was accrued elsewhere, without the identity of the other school
                // (spec §4.3). A school id resolves through GET /schools/{id}, which a system
                // administrator — which the stub identity is — would resolve.
                Origin = attendance.SchoolId == currentSchoolId ? CurrentSchool : OtherSchool,
                LastUpdatedAt = attendance.ModifiedAt ?? attendance.CreatedAt
            };
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            // Mapped relative to the api/v1 group, and student-scoped rather than nested under
            // /schools/{schoolId}: the rows span schools by design (conventions §1, spec §4.2), so a
            // school segment would imply a tenant scope the query deliberately does not apply.
            //
            // No :guid route constraint. With one, a non-Guid {studentId} is a routing miss and
            // answers 404 SYSTEM.NOT_FOUND; without one it is a binding failure and answers 400
            // SYSTEM.MALFORMED_REQUEST, which is the row spec §7 documents. tasks.md T08-08 spells
            // the pattern with ":guid"; spec §7 and §9, and conventions §1, do not, and the spec wins.
            app.MapGet("/students/{studentId}/attendance", async (
                    Guid studentId,
                    int? schoolYear,
                    DateOnly? from,
                    DateOnly? toExclusive,
                    int? page,
                    int? pageSize,
                    IMediator mediator,
                    CancellationToken cancellationToken) =>
                {
                    // Every parameter name here is also a Query property name. The kernel infers a
                    // violation's `source` by matching the property path against the request's query
                    // keys, so a rename on either side silently degrades `"source": "query"` into
                    // whatever the fallback answers (spec §7).
                    PagedResponse<Response> history = await mediator.Send(
                        new Query
                        {
                            StudentId = studentId,
                            SchoolYear = schoolYear,
                            From = from,
                            ToExclusive = toExclusive,
                            Page = page,
                            PageSize = pageSize
                        },
                        cancellationToken);

                    return Results.Ok(history);
                })
                .WithName(nameof(GetStudentAttendanceHistory))
                .WithTags("Attendance")
                .Produces<PagedResponse<Response>>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
    }
}
