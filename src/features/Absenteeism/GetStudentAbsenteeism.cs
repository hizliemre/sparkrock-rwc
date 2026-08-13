using System.Text.Json.Serialization;
using Carter;
using domain.Attendance;
using domain.Exceptions;
using domain.Schools;
using domain.Security;
using domain.ValueObjects;
using FluentValidation;
using infra.persistence.sql;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace features.Absenteeism;

/// <summary>
///     <c>GET /students/{studentId}/absenteeism</c> — one student's chronic-absenteeism status for
///     one school year, and the home of the six absenteeism members the school-wide worklist reuses.
/// </summary>
/// <remarks>
///     <b>Chronic absenteeism here is <c>totalAbsences &gt;= threshold</c> — a raw count, not a
///     percentage of enrolled days.</b> That is legacy's semantic (<c>sp_GetStudentAttendance:40</c>)
///     and is preserved deliberately. There is no <c>rate</c>, no <c>enrolledDays</c> and no
///     denominator anywhere in the response, because the system has no honest enrolment figure and a
///     field that invited one would be fabricated (spec §1).
///     <para>
///         <b>The route is student-scoped, not school-nested</b> (conventions §1): the figure spans
///         schools within the school year (V-07c), so there is no one school it belongs under.
///     </para>
///     <para>
///         <b>VC-31 governs the query and getting it wrong is a runtime 500, not a compile error.</b>
///         <c>SchoolYearStart</c> is a value-converted property: the whole value may be compared in a
///         predicate, and <c>.StartYear</c> may be read only in a projection. EF InMemory evaluates
///         predicates in LINQ-to-Objects and therefore <i>passes</i> the illegal form, so the
///         translation assertion lives in <c>features.integration.tests/Absenteeism</c> and nowhere
///         else.
///     </para>
/// </remarks>
public static partial class GetStudentAbsenteeism
{
    /// <summary>
    ///     The threshold governing this figure came from the student's <b>current</b> school.
    /// </summary>
    /// <remarks>
    ///     DEC-16. Under F09's route set this is the only reachable value — the single read
    ///     authorises against <c>Student.SchoolId</c> and the worklist selects on it, so current and
    ///     requesting always coincide. The discriminator ships anyway: adding one after clients exist
    ///     is a breaking change, and it becomes load-bearing the moment any route reports a figure
    ///     for a student outside the caller's school. Reported to the design owner rather than
    ///     simplified away (spec §4.2).
    /// </remarks>
    public const string CurrentSchoolThresholdSource = "currentSchool";

    /// <summary>
    ///     The threshold came from the school that asked, not the student's current one.
    /// </summary>
    /// <remarks>
    ///     Declared because DEC-16 names both values and the pair is the contract. No F09 route
    ///     reaches it; see <see cref="CurrentSchoolThresholdSource" />.
    /// </remarks>
    public const string RequestingSchoolThresholdSource = "requestingSchool";

    /// <summary>
    ///     The <c>?schoolYear=</c> rule, shared with the worklist so the two cannot disagree.
    /// </summary>
    /// <remarks>
    ///     Bounded by <see cref="SchoolYear.MinStartYear" />/<see cref="SchoolYear.MaxStartYear" />
    ///     rather than by literals — a third copy of <c>1900..2100</c> is exactly L-10's shape. The
    ///     rule is what keeps <c>?schoolYear=1899</c> a 400: <c>SchoolYear.FromStartYear</c> throws
    ///     <see cref="ArgumentOutOfRangeException" />, which reaches no <c>IExceptionHandler</c> and
    ///     would surface as a 500.
    /// </remarks>
    internal static IRuleBuilderOptions<T, int?> ValidSchoolYear<T>(this IRuleBuilder<T, int?> rule) =>
        rule.InclusiveBetween(SchoolYear.MinStartYear, SchoolYear.MaxStartYear)
            .WithErrorCode(ErrorCodes.Validation.Failed)
            .WithMessage(FormattableString.Invariant(
                $"School year must be between {SchoolYear.MinStartYear} and {SchoolYear.MaxStartYear}."));

    /// <summary>
    ///     Resolves the school year to report on: the requested one, or the one containing
    ///     <b>school-local today</b>.
    /// </summary>
    /// <remarks>
    ///     DEC-12. <c>UtcNow.Date</c> rolls over mid-afternoon for a western school, which would put
    ///     a request made on 31 August into the wrong school year for a whole evening.
    /// </remarks>
    internal static SchoolYear ResolveSchoolYear(int? requested, School school, TimeProvider timeProvider)
    {
        if (requested is not null)
            return SchoolYear.FromStartYear(requested.Value);

        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(school.TimeZoneId);
        DateTimeOffset schoolLocalNow = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), zone);

        return SchoolYear.FromLocalDate(DateOnly.FromDateTime(schoolLocalNow.DateTime));
    }

    /// <summary>
    ///     The summary read, as an <see cref="IQueryable{T}" /> the handler executes and the
    ///     integration tier asserts the translation of.
    /// </summary>
    /// <remarks>
    ///     Exposed rather than inlined so the VC-31 assertions run over <b>this</b> query and not a
    ///     hand-written equivalent — a hand-written query is a test of the test, and the mistake this
    ///     guards against is invisible at the handler tier (spec §8).
    ///     <para>
    ///         Both halves of VC-31 are here, four lines apart. The <c>Where</c> compares the whole
    ///         converted value; the <c>Select</c> is the only position from which <c>.StartYear</c>
    ///         translates. Swapping which one reaches into the member is an
    ///         <see cref="InvalidOperationException" /> on Npgsql and a 500 on a graded endpoint.
    ///     </para>
    /// </remarks>
    internal static IQueryable<SummaryRow> SummaryQuery(
        IDbContext dbContext,
        Guid studentId,
        SchoolYear schoolYear) =>
        dbContext.StudentAttendanceSummaries
            .AsNoTracking()
            .Where(row => row.StudentId == studentId && row.SchoolYearStart == schoolYear)
            .Select(row => new SummaryRow
            {
                TotalAbsences = row.TotalAbsences,
                StartYear = row.SchoolYearStart.StartYear,
                LastUpdatedAt = row.ModifiedAt ?? row.CreatedAt
            });

    /// <summary>
    ///     Whether any absence in the year was recorded by a school other than the subject's own.
    /// </summary>
    /// <remarks>
    ///     DEC-16's read-side sign-off marker, computed rather than a constant <c>true</c> so a UI
    ///     renders the caveat only where it applies — a banner on every response is a banner nobody
    ///     reads. It never names the other school.
    ///     <para>
    ///         The range comes from <see cref="SchoolYear.ToDateRange" /> against <c>AttendDate</c>,
    ///         which is the sargable form; comparing a computed year against the date column is V-12.
    ///         The soft-delete filter applies automatically (VC-13).
    ///     </para>
    /// </remarks>
    internal static Task<bool> HasOtherSchoolAbsenceAsync(
        IDbContext dbContext,
        Guid studentId,
        Guid schoolId,
        SchoolYear schoolYear,
        CancellationToken cancellationToken)
    {
        (DateOnly from, DateOnly toExclusive) = schoolYear.ToDateRange();

        return dbContext.StudentAttendances
            .AsNoTracking()
            .Where(attendance => attendance.StudentId == studentId)
            .Where(attendance => attendance.AttendDate >= from && attendance.AttendDate < toExclusive)
            .AnyAsync(attendance => attendance.IsAbsent && attendance.SchoolId != schoolId, cancellationToken);
    }

    /// <summary>What the summary query reads. Never the entity — the projection is the contract.</summary>
    internal sealed record SummaryRow
    {
        public required int TotalAbsences { get; init; }

        /// <summary>VC-31's legal half: member access on a converted value, in a projection.</summary>
        public required int StartYear { get; init; }

        public required DateTimeOffset LastUpdatedAt { get; init; }
    }

    public sealed class Query : IRequest<Response>
    {
        /// <summary>From the route.</summary>
        public required Guid StudentId { get; init; }

        /// <summary>
        ///     Named exactly as its query key, or <c>api/Errors/ViolationSource</c> stops inferring
        ///     <c>"source": "query"</c>.
        /// </summary>
        public int? SchoolYear { get; init; }
    }

    public sealed record Response
    {
        public required Guid StudentId { get; init; }

        /// <summary>The int start year. Read from the projection, never recomputed client-side.</summary>
        public required int SchoolYear { get; init; }

        /// <summary><c>SchoolYear.ToString()</c> — conventions §2 requires both spellings.</summary>
        public required string SchoolYearLabel { get; init; }

        public required int TotalAbsences { get; init; }

        public required int Threshold { get; init; }

        /// <summary>
        ///     <c>"currentSchool"</c> or <c>"requestingSchool"</c>. Never a school id or name — DEC-16
        ///     removed <c>thresholdSourceSchoolId</c> because it discloses where a child transferred to.
        /// </summary>
        public required string ThresholdSource { get; init; }

        public required bool IsChronicallyAbsent { get; init; }

        public required bool IncludesOtherSchoolAbsences { get; init; }

        /// <summary>
        ///     The summary's <c>ModifiedAt ?? CreatedAt</c> (V-21), omitted when no summary row
        ///     exists — there is nothing to date.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? LastUpdatedAt { get; init; }
    }

    internal sealed class QueryValidator : AbstractValidator<Query>
    {
        public QueryValidator() => RuleFor(query => query.SchoolYear).ValidSchoolYear();
    }

    internal sealed class QueryHandler(IDbContext dbContext, ICurrentUser currentUser, TimeProvider timeProvider)
        : IRequestHandler<Query, Response>
    {
        public async Task<Response> Handle(Query request, CancellationToken cancellationToken)
        {
            // Unscoped, then the membership check — spec §4.4. Both paths raise the same exception
            // with the same code and no message, so an unauthorised caller and a caller naming a
            // student that does not exist get byte-identical payloads.
            Guid? subjectSchoolId = await dbContext.Students
                .AsNoTracking()
                .Where(student => student.Id == request.StudentId)
                .Select(student => (Guid?)student.SchoolId)
                .FirstOrDefaultAsync(cancellationToken);

            if (subjectSchoolId is null)
                throw new NotFoundException(ErrorCodes.Student.NotFound);

            Guid schoolId = subjectSchoolId.Value;

            // DEC-16: access follows Student.SchoolId, not the summary's school of record.
            currentUser.EnsureAuthorized(schoolId, ErrorCodes.Student.NotFound);

            // The student's *current* school, which is where the governing threshold and the
            // school-local day both come from. Sourcing either through summary.SchoolId is what
            // DEC-16 overrides, and is what the legacy join did.
            School school = await dbContext.Schools
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == schoolId, cancellationToken)
                ?? throw new NotFoundException(ErrorCodes.Student.NotFound);

            SchoolYear schoolYear = ResolveSchoolYear(request.SchoolYear, school, timeProvider);

            // AbsenceRules is a pure function and does not translate, so the threshold is resolved
            // here, before the query. V-26's single copy of legacy's ISNULL(…, 10).
            int threshold = AbsenceRules.ResolveThreshold(school.AbsenceAlertThreshold);

            SummaryRow? summary = await SummaryQuery(dbContext, request.StudentId, schoolYear)
                .FirstOrDefaultAsync(cancellationToken);

            // No summary row means zero, not 404 (spec §3). The addressed resource is the student.
            int totalAbsences = summary?.TotalAbsences ?? 0;

            bool includesOtherSchoolAbsences = await HasOtherSchoolAbsenceAsync(
                dbContext, request.StudentId, schoolId, schoolYear, cancellationToken);

            return new Response
            {
                StudentId = request.StudentId,
                SchoolYear = summary?.StartYear ?? schoolYear.StartYear,
                SchoolYearLabel = schoolYear.ToString(),
                TotalAbsences = totalAbsences,
                Threshold = threshold,
                ThresholdSource = CurrentSchoolThresholdSource,
                IsChronicallyAbsent = AbsenceRules.IsChronicallyAbsent(
                    totalAbsences, school.AbsenceAlertThreshold),
                IncludesOtherSchoolAbsences = includesOtherSchoolAbsences,
                LastUpdatedAt = summary?.LastUpdatedAt
            };
        }
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            app.MapGet("/students/{studentId:guid}/absenteeism", async (
                    Guid studentId,
                    int? schoolYear,
                    IMediator mediator,
                    CancellationToken cancellationToken) =>
                {
                    Response response = await mediator.Send(
                        new Query { StudentId = studentId, SchoolYear = schoolYear },
                        cancellationToken);

                    return Results.Ok(response);
                })
                .WithName(nameof(GetStudentAbsenteeism))
                .WithTags("Absenteeism")
                .Produces<Response>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
    }
}
