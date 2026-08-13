using System.Linq.Expressions;
using System.Text.Json.Serialization;
using Carter;
using domain.Attendance;
using domain.Exceptions;
using domain.Schools;
using domain.Security;
using infra.persistence.sql;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace features.Schools;

/// <summary>
///     <c>GET /schools/{schoolId}</c>, and the home of the <see cref="Response" /> every Schools slice
///     returns.
/// </summary>
/// <remarks>
///     The response shape is declared once here rather than per slice, so a field added to a school is
///     added in one place and appears on the create, update and list payloads at the same time.
/// </remarks>
public static partial class GetSchoolById
{
    /// <summary>
    ///     The projection every Schools slice reads through, so the five routes cannot drift.
    /// </summary>
    /// <remarks>
    ///     An <see cref="Expression{TDelegate}" /> rather than a method, because it has to translate to
    ///     SQL for the list and single-item reads. <see cref="ToResponse" /> compiles it once for the
    ///     write slices, which already hold the tracked entity and have nothing to translate.
    /// </remarks>
    internal static readonly Expression<Func<School, Response>> Projection = school => new Response
    {
        Id = school.Id,
        Name = school.Name,
        TimeZoneId = school.TimeZoneId,
        AbsenceAlertThreshold = school.AbsenceAlertThreshold,
        IsActive = school.IsActive,
        CreatedAt = school.CreatedAt,

        // V-21's global rule. The interceptor leaves ModifiedAt null until a row is actually
        // modified, so an unmodified row would otherwise report no timestamp at all.
        LastUpdatedAt = school.ModifiedAt ?? school.CreatedAt
    };

    private static readonly Func<School, Response> CompiledProjection = Projection.Compile();

    public sealed class Query : IRequest<Response>
    {
        public required Guid SchoolId { get; init; }
    }

    public sealed record Response
    {
        public required Guid Id { get; init; }

        public required string Name { get; init; }

        public required string TimeZoneId { get; init; }

        /// <summary>
        ///     The school's own threshold, or absent when it has not configured one.
        /// </summary>
        /// <remarks>
        ///     Omitted rather than serialised as <c>null</c> (conventions §2). Nothing in the kernel
        ///     configures <c>WhenWritingNull</c> globally, and a global change would reshape every
        ///     other feature's payloads, so the rule is applied per property.
        /// </remarks>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? AbsenceAlertThreshold { get; init; }

        /// <summary>
        ///     The threshold actually governing this school. Always present.
        /// </summary>
        /// <remarks>
        ///     Computed rather than projected: <see cref="AbsenceRules.ResolveThreshold" /> is a pure
        ///     function and does not translate to SQL, and inlining <c>?? 10</c> into the projection
        ///     would be the second copy of the default that this migration exists to remove (L-10).
        ///     <para>
        ///         It exists so a client never has to know that null means ten. Because it is computed
        ///         after materialisation it cannot be filtered or sorted on.
        ///     </para>
        /// </remarks>
        public int EffectiveAbsenceAlertThreshold => AbsenceRules.ResolveThreshold(AbsenceAlertThreshold);

        public required bool IsActive { get; init; }

        public required DateTimeOffset CreatedAt { get; init; }

        public required DateTimeOffset LastUpdatedAt { get; init; }
    }

    /// <summary>Projects an already-materialised school, for the write slices.</summary>
    internal static Response ToResponse(School school) => CompiledProjection(school);

    internal sealed class QueryHandler(IDbContext dbContext, ICurrentUser currentUser)
        : IRequestHandler<Query, Response>
    {
        public async Task<Response> Handle(Query request, CancellationToken cancellationToken)
        {
            // Scope first, then existence. A privilege or existence answer given before the scope
            // check would be an oracle for a school the caller may not see. School's scope key is its
            // own Id, so WhereAuthorized — which needs ISchoolScoped — does not apply.
            currentUser.EnsureAuthorized(request.SchoolId, ErrorCodes.School.NotFound);

            Response? response = await dbContext.Schools
                .AsNoTracking()
                .Where(school => school.Id == request.SchoolId)
                .Select(Projection)
                .FirstOrDefaultAsync(cancellationToken);

            // Inactive schools return 200 with isActive: false. DEC-19 makes deactivation hide a
            // school from default list results and nothing else.
            return response ?? throw new NotFoundException(ErrorCodes.School.NotFound);
        }
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            app.MapGet("/schools/{schoolId}", async (
                    Guid schoolId,
                    IMediator mediator,
                    CancellationToken cancellationToken) =>
                {
                    Response response = await mediator.Send(new Query { SchoolId = schoolId }, cancellationToken);

                    return Results.Ok(response);
                })
                .WithName(nameof(GetSchoolById))
                .WithTags("Schools")
                .Produces<Response>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
    }
}
