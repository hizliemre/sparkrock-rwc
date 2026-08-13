using System.Linq.Expressions;
using System.Text.Json.Serialization;
using Carter;
using domain.Exceptions;
using domain.Security;
using domain.Students;
using infra.persistence.sql;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace features.Students;

/// <summary>
///     <c>GET /schools/{schoolId}/students/{studentId}</c>, and the home of the <see cref="Response" />
///     every Students slice returns.
/// </summary>
/// <remarks>
///     The response shape is declared once here rather than per slice, so a field added to a student
///     is added in one place and appears on the create, update and list payloads at the same time.
///     <para>
///         <b>The query is keyed on both ids.</b> A student of another school and a student that does
///         not exist are then the same query returning no rows — indistinguishable by construction
///         rather than by call-site discipline (spec §4, the existence-oracle rule applied to a path
///         resource).
///     </para>
/// </remarks>
public static partial class GetStudentById
{
    /// <summary>
    ///     The projection every Students slice reads through, so the five routes cannot drift.
    /// </summary>
    /// <remarks>
    ///     An <see cref="Expression{TDelegate}" /> rather than a method, because it has to translate to
    ///     SQL for the roster and single-item reads. <see cref="ToResponse" /> compiles it once for the
    ///     write slices, which already hold the tracked entity and have nothing to translate.
    ///     <para>
    ///         <c>LegacyId</c> is deliberately absent (DEC-02). It is a sequential integer, and
    ///         exposing it on the one aggregate that identifies children restores exactly the
    ///         enumeration Guid keys were chosen to prevent. There is no <c>DateOfBirth</c> to expose.
    ///     </para>
    /// </remarks>
    internal static readonly Expression<Func<Student, Response>> Projection = student => new Response
    {
        Id = student.Id,
        SchoolId = student.SchoolId,
        FirstName = student.FirstName,
        LastName = student.LastName,
        Grade = student.Grade,
        IsActive = student.IsActive,
        CreatedAt = student.CreatedAt,

        // V-21's global rule. The interceptor leaves ModifiedAt null until a row is actually
        // modified, so an unmodified row would otherwise report no timestamp at all.
        LastUpdatedAt = student.ModifiedAt ?? student.CreatedAt
    };

    private static readonly Func<Student, Response> CompiledProjection = Projection.Compile();

    public sealed class Query : IRequest<Response>
    {
        /// <summary>From the route. The scope key every F05 route is nested under.</summary>
        public required Guid SchoolId { get; init; }

        public required Guid StudentId { get; init; }
    }

    public sealed record Response
    {
        public required Guid Id { get; init; }

        public required Guid SchoolId { get; init; }

        public required string FirstName { get; init; }

        public required string LastName { get; init; }

        /// <summary>
        ///     The student's grade, or absent when they have none.
        /// </summary>
        /// <remarks>
        ///     Omitted rather than serialised as <c>null</c> (conventions §2). Nothing in the kernel
        ///     configures <c>WhenWritingNull</c> globally, and a global change would reshape every
        ///     other feature's payloads, so the rule is applied per property.
        /// </remarks>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Grade { get; init; }

        public required bool IsActive { get; init; }

        public required DateTimeOffset CreatedAt { get; init; }

        public required DateTimeOffset LastUpdatedAt { get; init; }
    }

    /// <summary>Projects an already-materialised student, for the write slices.</summary>
    internal static Response ToResponse(Student student) => CompiledProjection(student);

    internal sealed class QueryHandler(IDbContext dbContext, ICurrentUser currentUser)
        : IRequestHandler<Query, Response>
    {
        public async Task<Response> Handle(Query request, CancellationToken cancellationToken)
        {
            // Scope first. An existence answer given before the scope check would be an oracle for a
            // school the caller may not see; for a non-admin an absent school is also outside scope,
            // so an unauthorised school and an absent one fail here with the same code and the same
            // (absent) message.
            currentUser.EnsureAuthorized(request.SchoolId, ErrorCodes.School.NotFound);

            // Both ids in one predicate. WhereAuthorized is deliberately not added on top: the query
            // is already keyed on a schoolId EnsureAuthorized approved, and two scoping mechanisms on
            // one query means neither is obviously the one doing the work.
            Response? response = await dbContext.Students
                .AsNoTracking()
                .Where(student => student.Id == request.StudentId && student.SchoolId == request.SchoolId)
                .Select(Projection)
                .FirstOrDefaultAsync(cancellationToken);

            // Inactive students return 200 with isActive: false. DEC-19 makes deactivation hide a
            // student from default roster results and nothing else.
            return response ?? throw new NotFoundException(ErrorCodes.Student.NotFound);
        }
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            app.MapGet("/schools/{schoolId}/students/{studentId}", async (
                    Guid schoolId,
                    Guid studentId,
                    IMediator mediator,
                    CancellationToken cancellationToken) =>
                {
                    Response response = await mediator.Send(
                        new Query { SchoolId = schoolId, StudentId = studentId },
                        cancellationToken);

                    return Results.Ok(response);
                })
                .WithName(nameof(GetStudentById))
                .WithTags("Students")
                .Produces<Response>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
    }
}
