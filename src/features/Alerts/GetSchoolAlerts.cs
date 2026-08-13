using System.Text.Json.Serialization;
using Carter;
using domain.Alerts;
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

namespace features.Alerts;

/// <summary>
///     <c>GET /schools/{schoolId}/alerts</c> — a school's alert worklist, and the home of the
///     <see cref="Response" /> <see cref="ResolveAlert" /> also returns.
/// </summary>
/// <remarks>
///     <b>The list is scoped by <c>Student.SchoolId</c>, never by <c>StudentAlert.SchoolId</c>.</b>
///     DEC-16 says "access follows <c>Student.SchoolId</c>; a former school loses access at transfer",
///     and this feature reads that literally. The two other candidate readings both fail, and both
///     fail in ways that are invisible on any data set without a transfer:
///     <list type="bullet">
///         <item>
///             <b>School of record</b> (<c>alert.SchoolId == schoolId</c>) keeps showing a
///             transferred child's alert to the school they left — forbidden by the same sentence of
///             DEC-16 that assigns access, and the disclosure V-28 exists to close.
///         </item>
///         <item>
///             <b>The intersection</b> of the two makes a prior-school episode invisible to
///             <em>everyone</em>, and it cannot auto-resolve either: F07 keys its recount on
///             <c>(StudentId, AlertType, SchoolYearStart, SchoolId)</c> with the <em>submitting</em>
///             school, which after a transfer is never the alert's school. The row stays open
///             forever, unseen — L-07 ("alerts never resolve") reinstated by construction.
///         </item>
///     </list>
///     <para>
///         Two consequences are user-visible and deliberate. A receiving school inherits the prior
///         school's open episode and can resolve it, so a transferred, chronically absent student can
///         show <b>two</b> open alerts in one list — DEC-16 keys suppression per school precisely so
///         the receiving school can raise its own, and it says notification-level deduplication is
///         where duplicates are collapsed. And a former school loses an alert it may have been
///         mid-way through triaging, which is V-28's accepted cost.
///     </para>
///     <para>
///         <c>WhereAuthorized(currentUser)</c> is therefore <b>not</b> used here, although
///         <c>StudentAlert</c> implements <c>ISchoolScoped</c>: it applies its predicate to
///         <c>alert.SchoolId</c>, which is the first rejected reading wearing the kernel's name.
///         Scope is asserted with <c>EnsureAuthorized</c> on the route value and then carried by the
///         student join. <c>Handle_ScopesByStudentCurrentSchoolNotAlertSchool</c> is the assertion.
///     </para>
///     <para>
///         <b>The alert's <c>SchoolId</c> is never returned</b> (<see cref="Response" />). DEC-16
///         forbids <c>thresholdSourceSchoolId</c> because it discloses where a child moved <em>to</em>;
///         a prior school's id discloses where they moved <em>from</em>, and a care placement or
///         domestic-abuse transfer is exactly the case where that must not flow. The
///         <see cref="Response.SchoolOfRecord" /> discriminator carries what a client needs to render
///         "raised at a previous school" and nothing else.
///     </para>
/// </remarks>
public static partial class GetSchoolAlerts
{
    /// <summary>The default <c>?status=</c>: what needs attention.</summary>
    public const string OpenStatus = "open";

    /// <summary>
    ///     <c>?status=resolved</c>: what was decided.
    /// </summary>
    /// <remarks>
    ///     There is deliberately no <c>all</c>. The two sets are ordered by different things in a
    ///     reader's head, and a mixed page where half the rows omit four fields is worse than two
    ///     requests.
    /// </remarks>
    public const string ResolvedStatus = "resolved";

    /// <summary>The alert names the student's current school.</summary>
    public const string CurrentSchoolOfRecord = "currentSchool";

    /// <summary>The alert was raised at a school the student has since left.</summary>
    public const string PriorSchoolOfRecord = "priorSchool";

    /// <summary>Which half of the episode lifecycle a request is asking for.</summary>
    public enum StatusFilter
    {
        /// <summary>No resolution recorded. The default.</summary>
        Open = 0,

        /// <summary>A resolution is recorded, by a person or by F07's recount.</summary>
        Resolved = 1
    }

    /// <summary>
    ///     Parses <c>?status=</c>. Absent is <see cref="StatusFilter.Open" />; anything unrecognised
    ///     is <c>false</c>, which the validator turns into a 400.
    /// </summary>
    /// <remarks>
    ///     The wire value is parsed here rather than bound as an enum by the framework. Minimal-API
    ///     enum binding turns <c>?status=all</c> into a <em>binding</em> failure, which never reaches
    ///     an <c>IExceptionHandler</c> and is stamped <c>SYSTEM.MALFORMED_REQUEST</c> — but spec §7
    ///     documents <c>VALIDATION.FAILED</c> for an unrecognised status, and a code the route table
    ///     promises has to be a code the route can actually raise. Matching is ordinal and
    ///     case-sensitive: <c>Open</c> and <c>open </c> are not the contract, and accepting spellings
    ///     the OpenAPI document does not list teaches clients to send them.
    /// </remarks>
    internal static bool TryParseStatus(string? value, out StatusFilter status)
    {
        switch (value)
        {
            case null:
            case OpenStatus:
                status = StatusFilter.Open;
                return true;

            case ResolvedStatus:
                status = StatusFilter.Resolved;
                return true;

            default:
                status = StatusFilter.Open;
                return false;
        }
    }

    /// <summary>
    ///     The worklist read — scope, status, year, drift and the total order — as an
    ///     <see cref="IQueryable{T}" /> the handler pages and the integration tier asserts the
    ///     translation of.
    /// </summary>
    /// <remarks>
    ///     Exposed rather than inlined for the reason F09's queries are: what matters about the drift
    ///     predicate is the SQL it produces, and asserting that against a hand-written equivalent
    ///     would be a test of the test.
    ///     <para>
    ///         The joins are explicit because F01d ships no navigation properties — and that is the
    ///         point: the join that supplies <c>currentThreshold</c> and <c>schoolOfRecord</c>, and
    ///         the <c>where</c> on the <em>middle</em> table that is this feature's whole access rule,
    ///         are visible in one read.
    ///     </para>
    ///     <para>
    ///         Soft-deleted alerts are excluded by the reflective query filter (DEC-20); nothing here
    ///         calls <c>IgnoreQueryFilters</c>, which is banned.
    ///     </para>
    /// </remarks>
    internal static IQueryable<Row> AlertRows(
        IDbContext dbContext,
        Guid schoolId,
        StatusFilter status,
        int? schoolYear,
        bool thresholdDrift)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        IQueryable<StudentAlert> alerts = dbContext.StudentAlerts.AsNoTracking();

        // By composition rather than as a disjunction over a parameter, so each branch is a plain
        // IS NULL / IS NOT NULL the planner can use.
        alerts = status == StatusFilter.Open
            ? alerts.Where(alert => alert.ResolvedAt == null)
            : alerts.Where(alert => alert.ResolvedAt != null);

        if (schoolYear is not null)
        {
            // VC-31: the *whole* converted value, compared against a parameter. Writing
            // `alert.SchoolYearStart.StartYear == schoolYear` here compiles, passes every handler-tier
            // test because EF InMemory evaluates predicates in LINQ-to-Objects, and throws
            // InvalidOperationException on Npgsql. `.StartYear` appears only in the projection below.
            SchoolYear year = SchoolYear.FromStartYear(schoolYear.Value);

            alerts = alerts.Where(alert => alert.SchoolYearStart == year);
        }

        IQueryable<Row> rows =
            from alert in alerts
            join student in dbContext.Students.AsNoTracking() on alert.StudentId equals student.Id
            join school in dbContext.Schools.AsNoTracking() on student.SchoolId equals school.Id

            // DEC-16, and the single most consequential line in this feature. Never
            // `alert.SchoolId == schoolId`, and never both.
            where student.SchoolId == schoolId
            select new Row
            {
                Id = alert.Id,
                StudentId = alert.StudentId,
                StudentFirstName = student.FirstName,
                StudentLastName = student.LastName,
                AlertType = alert.AlertType,
                SchoolYearStart = alert.SchoolYearStart.StartYear,
                AbsenceCount = alert.AbsenceCount,
                ThresholdAtRaise = alert.ThresholdAtRaise,

                // The SQL-expressible form of AbsenceRules.ResolveThreshold. The pure function cannot
                // be called inside a translated expression, so `??` re-expresses it — but only the
                // `??`, never the number: AbsenceRules.DefaultThreshold is a const int and is captured
                // as a literal, so `10` still appears exactly once in the solution (V-26, L-10).
                // Handle_WhenSchoolThresholdIsNull_UsesTheDomainDefaultForDrift is the guard.
                //
                // Deliberately not ResolveThreshold applied after materialisation: that function
                // *throws* on a non-positive configured threshold, which no constraint prevents being
                // stored, and a listing that 500s on one school's bad row is worse than a listing that
                // reports it. The filter below and this projection must also be the same expression,
                // or the flag and the filtered set can disagree.
                CurrentThreshold = school.AbsenceAlertThreshold ?? AbsenceRules.DefaultThreshold,

                // The school id itself stays in the row and in the audit record; only the
                // discriminator leaves.
                IsCurrentSchoolOfRecord = alert.SchoolId == student.SchoolId,

                // Projected from CreatedAt: StudentAlert has no dedicated raise-instant column and F10
                // may not add one (risk R-3). Correct because an alert row is written once at raise
                // and a re-raise is a new row.
                RaisedAt = alert.CreatedAt,
                ResolvedAt = alert.ResolvedAt,
                ResolvedBy = alert.ResolvedBy,
                ResolutionSource = alert.ResolutionSource,
                ResolutionReason = alert.ResolutionReason
            };

        // DEC-18's triage query. Written over the *projected* row rather than over the joined
        // entities so that the filter and the `thresholdDrift` flag are literally the same
        // expression and cannot disagree — the failure mode being repaired is a second copy of the
        // rule, not a second copy of the number.
        if (thresholdDrift)
            rows = rows.Where(row => row.ThresholdAtRaise != row.CurrentThreshold);

        // Newest first: an alert list is a work queue. Total, ending in Id — a non-total order under
        // the global SplitQuery setting can repeat a row on one page and drop another (VC-27).
        return rows
            .OrderByDescending(row => row.RaisedAt)
            .ThenBy(row => row.Id);
    }

    /// <summary>
    ///     The same <see cref="Row" />, built from entities already in memory, for
    ///     <see cref="ResolveAlert" />'s 200 body.
    /// </summary>
    /// <remarks>
    ///     A second construction of the same shape, which is a drift risk — so
    ///     <c>ToRow_MatchesTheProjectedRow</c> asserts it equals what
    ///     <see cref="AlertRows" /> projects for the same three rows. <see cref="Row" /> is a record,
    ///     so that one equality covers every member, including members added later.
    /// </remarks>
    internal static Row ToRow(StudentAlert alert, Student student, School school)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentNullException.ThrowIfNull(student);
        ArgumentNullException.ThrowIfNull(school);

        return new Row
        {
            Id = alert.Id,
            StudentId = alert.StudentId,
            StudentFirstName = student.FirstName,
            StudentLastName = student.LastName,
            AlertType = alert.AlertType,
            SchoolYearStart = alert.SchoolYearStart.StartYear,
            AbsenceCount = alert.AbsenceCount,
            ThresholdAtRaise = alert.ThresholdAtRaise,
            CurrentThreshold = school.AbsenceAlertThreshold ?? AbsenceRules.DefaultThreshold,
            IsCurrentSchoolOfRecord = alert.SchoolId == student.SchoolId,
            RaisedAt = alert.CreatedAt,
            ResolvedAt = alert.ResolvedAt,
            ResolvedBy = alert.ResolvedBy,
            ResolutionSource = alert.ResolutionSource,
            ResolutionReason = alert.ResolutionReason
        };
    }

    /// <summary>Maps the projected row onto the wire contract. The one place either route renders.</summary>
    internal static Response ToResponse(Row row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new Response
        {
            Id = row.Id,
            StudentId = row.StudentId,
            StudentFirstName = row.StudentFirstName,
            StudentLastName = row.StudentLastName,
            AlertType = row.AlertType.ToString(),
            SchoolYear = row.SchoolYearStart,
            SchoolYearLabel = SchoolYear.FromStartYear(row.SchoolYearStart).ToString(),
            AbsenceCount = row.AbsenceCount,
            ThresholdAtRaise = row.ThresholdAtRaise,
            CurrentThreshold = row.CurrentThreshold,
            ThresholdDrift = row.ThresholdAtRaise != row.CurrentThreshold,
            SchoolOfRecord = row.IsCurrentSchoolOfRecord ? CurrentSchoolOfRecord : PriorSchoolOfRecord,
            Status = row.ResolvedAt is null ? OpenStatus : ResolvedStatus,
            RaisedAt = row.RaisedAt,
            ResolvedAt = row.ResolvedAt,
            ResolvedBy = row.ResolvedBy,
            ResolutionSource = row.ResolutionSource?.ToString(),
            ResolutionReason = row.ResolutionReason
        };
    }

    /// <summary>
    ///     What the query reads. Never an entity — the projection is the contract, and an entity here
    ///     would carry <c>SchoolId</c> past the boundary that exists to stop it.
    /// </summary>
    internal sealed record Row
    {
        public required Guid Id { get; init; }

        public required Guid StudentId { get; init; }

        public required string StudentFirstName { get; init; }

        public required string StudentLastName { get; init; }

        public required AlertType AlertType { get; init; }

        /// <summary>VC-31's legal half: member access on a converted value, in a projection.</summary>
        public required int SchoolYearStart { get; init; }

        public required int AbsenceCount { get; init; }

        public required int ThresholdAtRaise { get; init; }

        /// <summary>The student's <b>current</b> school's effective threshold (DEC-16).</summary>
        public required int CurrentThreshold { get; init; }

        /// <summary>
        ///     Whether the alert's school is the student's current one. A <c>bool</c>, not the id —
        ///     the id must not reach a response.
        /// </summary>
        public required bool IsCurrentSchoolOfRecord { get; init; }

        public required DateTimeOffset RaisedAt { get; init; }

        public DateTimeOffset? ResolvedAt { get; init; }

        public Guid? ResolvedBy { get; init; }

        public ResolutionSource? ResolutionSource { get; init; }

        public string? ResolutionReason { get; init; }
    }

    public sealed class Query : IRequest<PagedResponse<Response>>
    {
        /// <summary>From the route. The scope key.</summary>
        public required Guid SchoolId { get; init; }

        /// <summary>
        ///     Property names match their query keys exactly, or <c>api/Errors/ViolationSource</c>
        ///     stops inferring <c>"source": "query"</c> (O-45).
        /// </summary>
        public string? Status { get; init; }

        public int? SchoolYear { get; init; }

        /// <summary>
        ///     DEC-18's triage filter. <c>true</c> narrows to stranded alerts; <c>false</c> and absent
        ///     are the same thing — every item carries the flag regardless.
        /// </summary>
        public bool? ThresholdDrift { get; init; }

        public int? Page { get; init; }

        public int? PageSize { get; init; }
    }

    /// <summary>
    ///     One alert, on the wire. Also <see cref="ResolveAlert" />'s 200 body.
    /// </summary>
    /// <remarks>
    ///     <b>No member names a school id</b>, asserted structurally by
    ///     <c>Response_DeclaresNoSchoolIdMember</c> rather than by inspecting a serialised body, so it
    ///     cannot be regressed by adding a field "for debugging".
    ///     <para>
    ///         <c>thresholdSource</c> from DEC-16 is deliberately absent: both routes are addressed by
    ///         the student's current school, so it would be the constant <c>"currentSchool"</c> on
    ///         every row. It belongs to F09, where the requesting and governing schools can differ.
    ///     </para>
    ///     <para>
    ///         <c>resolutionReason</c> <em>is</em> returned. It is free text written by a human, and it
    ///         is not <c>Notes</c> — conventions §2's prohibition names <c>Notes</c> and scopes itself
    ///         to errors, logs and telemetry (O-17). A resolution reason nobody can read is a
    ///         resolution with no audit value. It never enters a log template (§4).
    ///     </para>
    ///     <para>
    ///         <c>resolvedBy</c> is a bare user id with no display name: there is no user directory,
    ///         and under the stub identity every resolution records the same constant Guid (V-16).
    ///         Stated rather than papered over with a fabricated name.
    ///     </para>
    /// </remarks>
    public sealed record Response
    {
        public required Guid Id { get; init; }

        public required Guid StudentId { get; init; }

        public required string StudentFirstName { get; init; }

        public required string StudentLastName { get; init; }

        public required string AlertType { get; init; }

        public required int SchoolYear { get; init; }

        public required string SchoolYearLabel { get; init; }

        public required int AbsenceCount { get; init; }

        /// <summary>
        ///     The threshold in force when the episode opened. Audit only (DEC-18) — returned because
        ///     it is the only way a client can explain why an alert exists at a count that no longer
        ///     meets the current threshold.
        /// </summary>
        public required int ThresholdAtRaise { get; init; }

        public required int CurrentThreshold { get; init; }

        /// <summary>
        ///     <c>thresholdAtRaise != currentThreshold</c>. Present on every item, not only when the
        ///     filter is applied, so a school sees the drift without having to ask for it.
        /// </summary>
        public required bool ThresholdDrift { get; init; }

        /// <summary><c>currentSchool</c> or <c>priorSchool</c>. Never a school id (DEC-16).</summary>
        public required string SchoolOfRecord { get; init; }

        public required string Status { get; init; }

        public required DateTimeOffset RaisedAt { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? ResolvedAt { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Guid? ResolvedBy { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ResolutionSource { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ResolutionReason { get; init; }
    }

    internal sealed class QueryValidator : AbstractValidator<Query>
    {
        public QueryValidator()
        {
            RuleFor(query => query.Status)
                .Must(status => TryParseStatus(status, out _))
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage(FormattableString.Invariant(
                    $"Status must be '{OpenStatus}' or '{ResolvedStatus}'."));

            // Bounded by the value object's own constants rather than by literals — a third copy of
            // 1900..2100 is L-10's shape. The rule is what keeps ?schoolYear=1899 a 400:
            // SchoolYear.FromStartYear throws ArgumentOutOfRangeException, which reaches no
            // IExceptionHandler and would surface as a 500.
            RuleFor(query => query.SchoolYear)
                .InclusiveBetween(SchoolYear.MinStartYear, SchoolYear.MaxStartYear)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage(FormattableString.Invariant(
                    $"School year must be between {SchoolYear.MinStartYear} and {SchoolYear.MaxStartYear}."));

            // The cross-property rule, written on ThresholdDrift rather than on Status so the
            // violation points at the parameter the caller must drop. A `When` that never evaluates
            // true is indistinguishable from a passing rule, so both directions are tested
            // (Validate_WhenThresholdDriftRequestedWithOpenStatus_Succeeds).
            RuleFor(query => query.ThresholdDrift)
                .Must(drift => drift != true)
                .When(query => TryParseStatus(query.Status, out StatusFilter status)
                               && status == StatusFilter.Resolved)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage("thresholdDrift applies to open alerts only.");

            RuleFor(query => query.Page).ValidPage();
            RuleFor(query => query.PageSize).ValidPageSize();
        }
    }

    internal sealed class QueryHandler(IDbContext dbContext, ICurrentUser currentUser)
        : IRequestHandler<Query, PagedResponse<Response>>
    {
        public async Task<PagedResponse<Response>> Handle(Query request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            // Scope resolves before anything is read, and an unauthorised school and an absent one
            // produce the same 404 with the same code and no message. ALERT.NOT_FOUND rather than
            // SCHOOL.NOT_FOUND: the addressed resource is this school's alert collection.
            currentUser.EnsureAuthorized(request.SchoolId, ErrorCodes.Alert.NotFound);

            // The validator has already rejected an unrecognised value; the discarded bool is that
            // contract restated rather than a second one.
            _ = TryParseStatus(request.Status, out StatusFilter status);

            PagedResponse<Row> page = await AlertRows(
                    dbContext,
                    request.SchoolId,
                    status,
                    request.SchoolYear,
                    request.ThresholdDrift == true)
                .ToPagedResponseAsync(request.Page, request.PageSize, cancellationToken);

            // An empty result is the envelope with totalItems: 0, never a 404.
            return new PagedResponse<Response>
            {
                Items = page.Items.Select(ToResponse).ToArray(),
                Page = page.Page
            };
        }
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            // Module-relative, never "/api/..." — UseSparkrockRwc mounts the group and the prefix
            // would double (conventions §1).
            app.MapGet("/schools/{schoolId:guid}/alerts", async (
                    Guid schoolId,
                    string? status,
                    int? schoolYear,
                    bool? thresholdDrift,
                    int? page,
                    int? pageSize,
                    IMediator mediator,
                    CancellationToken cancellationToken) =>
                {
                    PagedResponse<Response> alerts = await mediator.Send(
                        new Query
                        {
                            SchoolId = schoolId,
                            Status = status,
                            SchoolYear = schoolYear,
                            ThresholdDrift = thresholdDrift,
                            Page = page,
                            PageSize = pageSize
                        },
                        cancellationToken);

                    return Results.Ok(alerts);
                })
                .WithName(nameof(GetSchoolAlerts))
                .WithTags("Alerts")
                .Produces<PagedResponse<Response>>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
    }
}
