using Carter;
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

namespace features.Students;

/// <summary>
///     <c>GET /schools/{schoolId}/students</c> — the roster of one school: paged, active-only by
///     default, optionally filtered to one grade.
/// </summary>
/// <remarks>
///     There is deliberately no <c>?q</c> name search. On an API with no authentication it is a
///     name-lookup oracle over children, no supplied requirement asks for one, and if one arrives it
///     arrives with authentication.
/// </remarks>
public static partial class GetStudents
{
    public sealed class Query : IRequest<PagedResponse<GetStudentById.Response>>
    {
        /// <summary>From the route. The scope key, and the only tenant predicate on this query.</summary>
        public required Guid SchoolId { get; init; }

        public int? Page { get; init; }

        public int? PageSize { get; init; }

        /// <summary>
        ///     Optional exact match on <c>Grade</c>, trimmed. Absent <b>or blank</b> means all grades.
        /// </summary>
        /// <remarks>
        ///     D-06's corrected reading. Legacy's <c>cboGrade</c> had no handler and <c>LoadStudents</c>
        ///     ran immediately after <c>cboGrade.Clear()</c>, so the roster procedure was called with
        ///     <c>''</c> every single time (L-15) — the filter never filtered, and "all grades" is the
        ///     only behaviour anyone ever observed.
        ///     <para>
        ///         <b>V-24 belongs to F06</b>, which applies the identical rule to the attendance
        ///         roster. F05 does not claim the divergence, but the two must not diverge: a roster
        ///         that filters differently from the student list for the same query string is a defect
        ///         neither feature's tests would catch. Nothing mechanically enforces it — there is no
        ///         shared function, because the two features filter different queries.
        ///     </para>
        /// </remarks>
        public string? Grade { get; init; }

        /// <summary>Defaults to false. Deactivated students are hidden from list results only (DEC-19).</summary>
        public bool IncludeInactive { get; init; }
    }

    internal sealed class QueryValidator : AbstractValidator<Query>
    {
        public QueryValidator()
        {
            RuleFor(query => query.Page).ValidPage();
            RuleFor(query => query.PageSize).ValidPageSize();

            // Matches the column width, so the filter cannot be longer than anything it can match.
            // A blank value is not a filter at all and is therefore not a failure.
            RuleFor(query => query.Grade)
                .MaximumLength(CreateStudent.MaxGradeLength)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage(FormattableString.Invariant(
                    $"Grade must not exceed {CreateStudent.MaxGradeLength} characters."));
        }
    }

    internal sealed class QueryHandler(IDbContext dbContext, ICurrentUser currentUser)
        : IRequestHandler<Query, PagedResponse<GetStudentById.Response>>
    {
        public async Task<PagedResponse<GetStudentById.Response>> Handle(
            Query request,
            CancellationToken cancellationToken)
        {
            // Scope first, then existence. Answering "no such school" before the scope check would be
            // an oracle; answering an empty page for an absent school would contradict conventions §2,
            // which makes an unresolvable path {id} a 404.
            currentUser.EnsureAuthorized(request.SchoolId, ErrorCodes.School.NotFound);

            bool schoolExists = await dbContext.Schools
                .AsNoTracking()
                .AnyAsync(school => school.Id == request.SchoolId, cancellationToken);

            // Not filtered on the school's own IsActive: an inactive school still serves its roster
            // (DEC-19). SCHOOL.INACTIVE belongs to F07's save path.
            if (!schoolExists)
                throw new NotFoundException(ErrorCodes.School.NotFound);

            // WhereAuthorized is redundant here — the query is already keyed on a schoolId that
            // EnsureAuthorized approved — and is deliberately not added: two scoping mechanisms on one
            // query means neither is obviously the one doing the work.
            IQueryable<Student> students = dbContext.Students
                .AsNoTracking()
                .Where(student => student.SchoolId == request.SchoolId);

            // By composition, never `student.IsActive || @includeInactive`: a disjunction against a
            // parameter defeats ix_students_school_id_is_active.
            if (!request.IncludeInactive)
                students = students.Where(student => student.IsActive);

            if (!string.IsNullOrWhiteSpace(request.Grade))
            {
                // Trimmed once, in memory, so the predicate is a plain column comparison rather than
                // a trim() applied per row. Exact match, not prefix or contains: Grade is a short
                // enumerated string, and a contains would make `1` match 10, 11 and 12.
                //
                // Students with a null Grade are therefore returned when ?grade is absent and never
                // when it is present. There is no ?grade=none sentinel — a magic string in the value
                // space of a free-text column is a bug waiting for a school that names a grade "none".
                string grade = request.Grade.Trim();

                students = students.Where(student => student.Grade == grade);
            }

            // Total order, and the order a register is read in. Last name and first name together are
            // not unique, and a non-total order under the global SplitQuery setting can repeat a row
            // on one page and drop another (VC-27), so the sort ends in Id.
            return await students
                .OrderBy(student => student.LastName)
                .ThenBy(student => student.FirstName)
                .ThenBy(student => student.Id)
                .Select(GetStudentById.Projection)
                .ToPagedResponseAsync(request.Page, request.PageSize, cancellationToken);
        }
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            app.MapGet("/schools/{schoolId}/students", async (
                    Guid schoolId,
                    int? page,
                    int? pageSize,
                    string? grade,
                    bool? includeInactive,
                    IMediator mediator,
                    CancellationToken cancellationToken) =>
                {
                    PagedResponse<GetStudentById.Response> students = await mediator.Send(
                        new Query
                        {
                            SchoolId = schoolId,
                            Page = page,
                            PageSize = pageSize,
                            Grade = grade,
                            IncludeInactive = includeInactive ?? false
                        },
                        cancellationToken);

                    return Results.Ok(students);
                })
                .WithName(nameof(GetStudents))
                .WithTags("Students")
                .Produces<PagedResponse<GetStudentById.Response>>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
    }
}
