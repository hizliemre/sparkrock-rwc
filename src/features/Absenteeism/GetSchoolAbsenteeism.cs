using System.Text.Json.Serialization;
using Carter;
using domain.Attendance;
using domain.Exceptions;
using domain.Schools;
using domain.Security;
using domain.Students;
using domain.ValueObjects;
using features.Paging;
using FluentValidation;
using infra.persistence.sql;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace features.Absenteeism;

/// <summary>
///     <c>GET /schools/{schoolId}/absenteeism</c> — one school's worklist of the same figure the
///     single-student read returns, worst first.
/// </summary>
/// <remarks>
///     Without this route nobody can answer "who in my school is chronically absent": the
///     single-student read needs the student first, so a school would enumerate its roster through
///     F05 and issue one request per student — and <c>?chronicOnly</c> could then only be applied
///     client-side, after paging.
///     <para>
///         <b>Rows are selected by <c>Student.SchoolId</c>, never by the summary's <c>SchoolId</c>.</b>
///         The summary's is school-of-record only (V-17, DEC-16) and F07 has not decided whether it
///         moves on transfer, so selecting on it would make this list depend on a policy F09 neither
///         owns nor can see. It also makes the list mean the right thing — the children currently in
///         this school — and makes <c>thresholdSource</c> invariantly <c>"currentSchool"</c>.
///     </para>
/// </remarks>
public static partial class GetSchoolAbsenteeism
{
    /// <summary>
    ///     The worklist read — roster, left-joined summaries, <c>chronicOnly</c> and the ordering —
    ///     as an <see cref="IQueryable{T}" /> the handler pages and the integration tier asserts the
    ///     translation of.
    /// </summary>
    /// <remarks>
    ///     Exposed rather than inlined for the reason
    ///     <see cref="GetStudentAbsenteeism.SummaryQuery" /> gives: the assertions that matter here
    ///     are about the SQL this query produces, and asserting them against a hand-written
    ///     equivalent would be a test of the test.
    /// </remarks>
    internal static IQueryable<Row> WorklistQuery(
        IDbContext dbContext,
        Guid schoolId,
        SchoolYear schoolYear,
        int threshold,
        bool chronicOnly,
        bool includeInactive)
    {
        IQueryable<Student> students = dbContext.Students
            .AsNoTracking()
            .Where(student => student.SchoolId == schoolId);

        // By composition, never `student.IsActive || @includeInactive`: a disjunction against a
        // parameter defeats ix_students_school_id_is_active.
        if (!includeInactive)
            students = students.Where(student => student.IsActive);

        // VC-31: the whole converted value in the predicate. Writing
        // `summary.SchoolYearStart.StartYear == year` here compiles, passes every test at the
        // handler tier, and throws InvalidOperationException on Npgsql.
        IQueryable<StudentAttendanceSummary> summaries = dbContext.StudentAttendanceSummaries
            .AsNoTracking()
            .Where(summary => summary.SchoolYearStart == schoolYear);

        IQueryable<Row> rows =
            from student in students
            join summary in summaries on student.Id equals summary.StudentId into matches
            from summary in matches.DefaultIfEmpty()
            select new Row
            {
                StudentId = student.Id,
                LastName = student.LastName,
                FirstName = student.FirstName,
                Grade = student.Grade,

                // The coalesce is load-bearing twice over: it makes "no summary" mean zero rather
                // than null, and it is the expression the ORDER BY below runs over.
                TotalAbsences = summary == null ? 0 : summary.TotalAbsences,
                LastUpdatedAt = summary == null
                    ? (DateTimeOffset?)null
                    : summary.ModifiedAt ?? summary.CreatedAt
            };

        // In the database, before paging. Filtering after materialisation would make
        // page.totalItems count the unfiltered set and deliver pages of varying size.
        if (chronicOnly)
            rows = rows.Where(row => row.TotalAbsences >= threshold);

        // Over the *coalesced* expression. Ordering on the nullable joined column would sort
        // students with no summary to the top of a worst-first worklist, because Postgres sorts
        // NULLS FIRST under ORDER BY … DESC. The sort ends in Id so the order is total — a
        // non-total order under the global SplitQuery setting can repeat a row on one page and
        // drop another (VC-27).
        return rows
            .OrderByDescending(row => row.TotalAbsences)
            .ThenBy(row => row.LastName)
            .ThenBy(row => row.FirstName)
            .ThenBy(row => row.StudentId);
    }

    /// <summary>The projected left-join row. Never an entity — the projection is the contract.</summary>
    internal sealed record Row
    {
        public required Guid StudentId { get; init; }

        public required string LastName { get; init; }

        public required string FirstName { get; init; }

        public string? Grade { get; init; }

        /// <summary>Coalesced, so a student with no summary is zero rather than null.</summary>
        public required int TotalAbsences { get; init; }

        public DateTimeOffset? LastUpdatedAt { get; init; }
    }

    public sealed class Query : IRequest<PagedResponse<Response>>
    {
        /// <summary>From the route. The scope key, and the only tenant predicate on this query.</summary>
        public required Guid SchoolId { get; init; }

        /// <summary>
        ///     Property names match their query keys exactly, or <c>api/Errors/ViolationSource</c>
        ///     stops inferring <c>"source": "query"</c>.
        /// </summary>
        public int? SchoolYear { get; init; }

        public bool ChronicOnly { get; init; }

        /// <summary>
        ///     Defaults to false, mirroring F05 exactly. A deactivated student who accrued 20
        ///     absences is exactly who a safeguarding worklist must not lose, and DEC-19 makes
        ///     <c>IsActive = false</c> hide a resource from default list results only.
        /// </summary>
        public bool IncludeInactive { get; init; }

        public int? Page { get; init; }

        public int? PageSize { get; init; }
    }

    /// <summary>
    ///     One row. The six absenteeism members are identical in name and meaning to
    ///     <see cref="GetStudentAbsenteeism.Response" />'s, so one client component renders both.
    /// </summary>
    /// <remarks>
    ///     <c>schoolYear</c> and <c>schoolYearLabel</c> are deliberately <b>not</b> repeated per row —
    ///     the year is in the request and 200 copies of it is noise. That is the only asymmetry with
    ///     the single read (spec §7.2).
    ///     <para>
    ///         <c>threshold</c> and <c>thresholdSource</c> do repeat per row rather than sitting at
    ///         list level: the envelope has no room for a third member, and DEC-16 makes both
    ///         per-student facts that merely happen to be constant across a single-school list.
    ///     </para>
    /// </remarks>
    public sealed record Response
    {
        public required Guid StudentId { get; init; }

        public required string LastName { get; init; }

        public required string FirstName { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Grade { get; init; }

        public required int TotalAbsences { get; init; }

        public required int Threshold { get; init; }

        public required string ThresholdSource { get; init; }

        public required bool IsChronicallyAbsent { get; init; }

        public required bool IncludesOtherSchoolAbsences { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? LastUpdatedAt { get; init; }
    }

    internal sealed class QueryValidator : AbstractValidator<Query>
    {
        public QueryValidator()
        {
            RuleFor(query => query.SchoolYear).ValidSchoolYear();
            RuleFor(query => query.Page).ValidPage();
            RuleFor(query => query.PageSize).ValidPageSize();
        }
    }

    internal sealed class QueryHandler(IDbContext dbContext, ICurrentUser currentUser, TimeProvider timeProvider)
        : IRequestHandler<Query, PagedResponse<Response>>
    {
        public async Task<PagedResponse<Response>> Handle(Query request, CancellationToken cancellationToken)
        {
            // Scope resolves before existence, so an unauthorised school and an absent one produce
            // the same 404 with the same code and no message.
            currentUser.EnsureAuthorized(request.SchoolId, ErrorCodes.School.NotFound);

            // Not filtered on IsActive: an inactive school still serves its worklist (DEC-19).
            School school = await dbContext.Schools
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == request.SchoolId, cancellationToken)
                ?? throw new NotFoundException(ErrorCodes.School.NotFound);

            SchoolYear schoolYear = GetStudentAbsenteeism.ResolveSchoolYear(
                request.SchoolYear, school, timeProvider);

            // Resolved once, before the query, which is the only reason a pure domain function can
            // drive a database filter: every student in this list is governed by the same school's
            // threshold, so `chronicOnly` closes over a plain int. Calling IsChronicallyAbsent inside
            // the predicate does not translate.
            int threshold = AbsenceRules.ResolveThreshold(school.AbsenceAlertThreshold);

            PagedResponse<Row> page = await WorklistQuery(
                    dbContext,
                    request.SchoolId,
                    schoolYear,
                    threshold,
                    request.ChronicOnly,
                    request.IncludeInactive)
                .ToPagedResponseAsync(request.Page, request.PageSize, cancellationToken);

            Guid[] pageStudentIds = page.Items.Select(row => row.StudentId).ToArray();

            HashSet<Guid> withOtherSchoolAbsences = await OtherSchoolAbsencesAsync(
                pageStudentIds, request.SchoolId, schoolYear, cancellationToken);

            return new PagedResponse<Response>
            {
                Items = page.Items
                    .Select(row => new Response
                    {
                        StudentId = row.StudentId,
                        LastName = row.LastName,
                        FirstName = row.FirstName,
                        Grade = row.Grade,
                        TotalAbsences = row.TotalAbsences,
                        Threshold = threshold,
                        ThresholdSource = GetStudentAbsenteeism.CurrentSchoolThresholdSource,
                        IsChronicallyAbsent = AbsenceRules.IsChronicallyAbsent(
                            row.TotalAbsences, school.AbsenceAlertThreshold),
                        IncludesOtherSchoolAbsences = withOtherSchoolAbsences.Contains(row.StudentId),
                        LastUpdatedAt = row.LastUpdatedAt
                    })
                    .ToArray(),
                Page = page.Page
            };
        }

        /// <summary>
        ///     One query over the page's student ids, issued <b>after</b> paging.
        /// </summary>
        /// <remarks>
        ///     Running it before paging would be an unbounded read; running it per row would be N
        ///     round trips under the globally configured <c>SplitQuery</c> (VC-27).
        ///     <c>Contains</c> over a runtime collection translates to <c>= ANY (@p)</c> for every
        ///     collection type (VC-30).
        /// </remarks>
        private async Task<HashSet<Guid>> OtherSchoolAbsencesAsync(
            Guid[] pageStudentIds,
            Guid schoolId,
            SchoolYear schoolYear,
            CancellationToken cancellationToken)
        {
            if (pageStudentIds.Length == 0)
                return [];

            (DateOnly from, DateOnly toExclusive) = schoolYear.ToDateRange();

            List<Guid> matched = await dbContext.StudentAttendances
                .AsNoTracking()
                .Where(attendance => pageStudentIds.Contains(attendance.StudentId))
                .Where(attendance => attendance.AttendDate >= from && attendance.AttendDate < toExclusive)
                .Where(attendance => attendance.IsAbsent && attendance.SchoolId != schoolId)
                .Select(attendance => attendance.StudentId)
                .Distinct()
                .ToListAsync(cancellationToken);

            return matched.ToHashSet();
        }
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            app.MapGet("/schools/{schoolId:guid}/absenteeism", async (
                    Guid schoolId,
                    int? schoolYear,
                    bool? chronicOnly,
                    bool? includeInactive,
                    int? page,
                    int? pageSize,
                    IMediator mediator,
                    CancellationToken cancellationToken) =>
                {
                    PagedResponse<Response> absenteeism = await mediator.Send(
                        new Query
                        {
                            SchoolId = schoolId,
                            SchoolYear = schoolYear,
                            ChronicOnly = chronicOnly ?? false,
                            IncludeInactive = includeInactive ?? false,
                            Page = page,
                            PageSize = pageSize
                        },
                        cancellationToken);

                    return Results.Ok(absenteeism);
                })
                .WithName(nameof(GetSchoolAbsenteeism))
                .WithTags("Absenteeism")
                .Produces<PagedResponse<Response>>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
    }
}
