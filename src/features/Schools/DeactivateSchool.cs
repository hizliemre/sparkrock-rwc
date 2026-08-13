using Carter;
using domain.Exceptions;
using domain.Schools;
using domain.Security;
using infra.persistence.sql;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace features.Schools;

/// <summary>
///     <c>DELETE /schools/{schoolId}</c> — deactivates, returns 204, and removes nothing.
/// </summary>
/// <remarks>
///     The shared <c>DELETE</c> handler contract, step for step:
///     <list type="number">
///         <item>resolve scope — <c>EnsureAuthorized</c>, path-school routes only</item>
///         <item>load by id, honouring scope, so a row outside scope is indistinguishable from absent</item>
///         <item>absent → <see cref="NotFoundException" /> → 404</item>
///         <item>privilege → <see cref="ActivationPolicy.Apply" /> → <see cref="ForbiddenException" /> → 403</item>
///         <item>already inactive → <c>Apply</c> returned false → return, no save, 204</item>
///         <item>otherwise → <c>Apply</c> set <c>IsActive = false</c> → save → 204</item>
///     </list>
///     <para>
///         <b>Never <c>Remove</c>.</b> <c>School</c> derives from <c>BaseEntity</c>, and the audit
///         interceptor throws <see cref="InvalidOperationException" /> for <c>EntityState.Deleted</c>
///         on anything that is not soft-deletable (DEC-20) — that surfaces as a 500, not as a delete.
///         The guard exists because EF's default cascade would otherwise physically delete the
///         school's students. Real erasure is DEC-19's audited purge, which has no owner yet.
///     </para>
/// </remarks>
public static partial class DeactivateSchool
{
    [LoggerMessage(
        EventId = 1102,
        Level = LogLevel.Information,
        Message = "School deactivated. SchoolId: {SchoolId}")]
    private static partial void LogSchoolDeactivated(ILogger logger, Guid schoolId);

    public sealed class Command : IRequest
    {
        public required Guid SchoolId { get; init; }
    }

    internal sealed class CommandHandler(
        IDbContext dbContext,
        ICurrentUser currentUser,
        ILogger<CommandHandler> logger)
        : IRequestHandler<Command>
    {
        public async Task Handle(Command request, CancellationToken cancellationToken)
        {
            currentUser.EnsureAuthorized(request.SchoolId, ErrorCodes.School.NotFound);

            School school = await dbContext.Schools
                                .FirstOrDefaultAsync(candidate => candidate.Id == request.SchoolId, cancellationToken)
                            ?? throw new NotFoundException(ErrorCodes.School.NotFound);

            // A DELETE commands the transition, so the privilege is checked before the row's current
            // state is consulted. An unprivileged DELETE on an already-inactive school is therefore a
            // 403 rather than an idempotent 204 — deliberate, so the status cannot be read as a state
            // oracle. See ActivationPolicy.
            if (!ActivationPolicy.Apply(
                    school,
                    requestedIsActive: false,
                    currentUser,
                    ActivationPrivilege.SystemAdmin,
                    nameof(School)))
            {
                // Already inactive. Saving here would stamp ModifiedAt through the interceptor and
                // report a change that did not happen.
                return;
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            LogSchoolDeactivated(logger, school.Id);
        }
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            app.MapDelete("/schools/{schoolId}", async (
                    Guid schoolId,
                    IMediator mediator,
                    CancellationToken cancellationToken) =>
                {
                    await mediator.Send(new Command { SchoolId = schoolId }, cancellationToken);

                    return Results.NoContent();
                })
                .WithName(nameof(DeactivateSchool))
                .WithTags("Schools")
                .Produces(StatusCodes.Status204NoContent)
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
    }
}
