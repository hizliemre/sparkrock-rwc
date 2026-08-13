using System.Linq.Expressions;
using Carter;
using domain.AttendanceCodes;
using domain.Exceptions;
using infra.persistence.sql;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace features.AttendanceCodes;

/// <summary>
///     <c>GET /attendance-codes/{codeId}</c>, and the home of the <see cref="Response" /> every
///     AttendanceCodes slice returns.
/// </summary>
/// <remarks>
///     The response shape is declared once here rather than per slice, so a field added to a code
///     appears on the create, update and list payloads at the same time.
///     <para>
///         <b>Unscoped by design (O-03).</b> The aggregate is global — conventions §1's "global, not
///         school-scoped" — so there is no <c>SchoolId</c>, no <c>EnsureAuthorized</c> and no
///         <c>WhereAuthorized</c> anywhere in F03. A list filtered by school here would be wrong twice
///         over: F07 accepts codes from a single global set, and a school that could not see a code
///         could still be sent one. 404 therefore means the id does not exist, full stop.
///     </para>
/// </remarks>
public static partial class GetAttendanceCodeById
{
    /// <summary>
    ///     The projection every AttendanceCodes slice reads through, so the five routes cannot drift.
    /// </summary>
    /// <remarks>
    ///     An <see cref="Expression{TDelegate}" /> rather than a method, because it has to translate to
    ///     SQL for the list and single-item reads. <see cref="ToResponse" /> compiles it once for the
    ///     write slices, which already hold the tracked entity and have nothing to translate.
    /// </remarks>
    internal static readonly Expression<Func<AttendanceCode, Response>> Projection = code => new Response
    {
        Id = code.Id,
        Value = code.Value,
        Description = code.Description,
        IsAbsent = code.IsAbsent,
        IsExcused = code.IsExcused,
        IsActive = code.IsActive,
        CreatedAt = code.CreatedAt,

        // V-21's global rule. The interceptor leaves ModifiedAt null until a row is actually
        // modified, so an unmodified row would otherwise report no timestamp at all.
        LastUpdatedAt = code.ModifiedAt ?? code.CreatedAt
    };

    private static readonly Func<AttendanceCode, Response> CompiledProjection = Projection.Compile();

    public sealed class Query : IRequest<Response>
    {
        public required Guid CodeId { get; init; }
    }

    /// <summary>
    ///     Spec §2. No nullable members, so no <c>[JsonIgnore]</c> is needed and every documented key
    ///     is always present. <c>LegacyId</c> never appears (DEC-02).
    /// </summary>
    public sealed record Response
    {
        public required Guid Id { get; init; }

        public required string Value { get; init; }

        public required string Description { get; init; }

        public required bool IsAbsent { get; init; }

        public required bool IsExcused { get; init; }

        public required bool IsActive { get; init; }

        public required DateTimeOffset CreatedAt { get; init; }

        public required DateTimeOffset LastUpdatedAt { get; init; }
    }

    /// <summary>Projects an already-materialised code, for the write slices.</summary>
    internal static Response ToResponse(AttendanceCode code) => CompiledProjection(code);

    /// <summary>
    ///     Takes <see cref="IDbContext" /> and nothing else.
    /// </summary>
    /// <remarks>
    ///     Deliberately no <c>ICurrentUser</c>: a handler that cannot reach the identity cannot scope
    ///     by it, which is the only form of "applies no tenant scope" that a test can actually hold
    ///     the code to. A named test asserts the constructor stays that way.
    /// </remarks>
    internal sealed class QueryHandler(IDbContext dbContext) : IRequestHandler<Query, Response>
    {
        public async Task<Response> Handle(Query request, CancellationToken cancellationToken)
        {
            Response? response = await dbContext.AttendanceCodes
                .AsNoTracking()
                .Where(code => code.Id == request.CodeId)
                .Select(Projection)
                .FirstOrDefaultAsync(cancellationToken);

            // Inactive codes return 200 with isActive: false. DEC-19 makes deactivation hide a code
            // from default list results and nothing else — F08 has to render history whose code was
            // deactivated afterwards, and a client needs somewhere to fetch its description.
            return response ?? throw new NotFoundException(ErrorCodes.AttendanceCode.NotFound);
        }
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            app.MapGet("/attendance-codes/{codeId}", async (
                    Guid codeId,
                    IMediator mediator,
                    CancellationToken cancellationToken) =>
                {
                    Response response = await mediator.Send(new Query { CodeId = codeId }, cancellationToken);

                    return Results.Ok(response);
                })
                .WithName(nameof(GetAttendanceCodeById))
                .WithTags("AttendanceCodes")
                .Produces<Response>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
    }
}
