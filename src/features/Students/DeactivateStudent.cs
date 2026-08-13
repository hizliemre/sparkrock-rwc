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
using Microsoft.Extensions.Logging;

namespace features.Students;

/// <summary>
///     <c>DELETE /schools/{schoolId}/students/{studentId}</c> — deactivates, returns 204, and removes
///     nothing.
/// </summary>
/// <remarks>
///     The shared <c>DELETE</c> handler contract, step for step:
///     <list type="number">
///         <item>resolve scope — <c>EnsureAuthorized</c>, path-school route</item>
///         <item>load by both ids, so a row outside scope is indistinguishable from absent</item>
///         <item>absent → <see cref="NotFoundException" /> → 404</item>
///         <item>privilege → <see cref="ActivationPolicy.Apply" /> — <c>SchoolScope</c>, so no 403</item>
///         <item>already inactive → <c>Apply</c> returned false → return, no save, 204</item>
///         <item>otherwise → <c>Apply</c> set <c>IsActive = false</c> → save → 204</item>
///     </list>
///     <para>
///         <b>Never <c>Remove</c>.</b> <c>Student</c> derives from <c>BaseEntity</c>, and the audit
///         interceptor throws <see cref="InvalidOperationException" /> for <c>EntityState.Deleted</c>
///         on anything that is not soft-deletable (DEC-20) — that surfaces as a 500, not as a delete.
///     </para>
///     <para>
///         <b>This is not an erasure path.</b> Deactivating a student removes them from nothing: their
///         attendance history, summaries and alerts stay readable (DEC-19), and F07's save pipeline
///         deliberately does not check whether a student is active, because legacy accepted attendance
///         for inactive students. DEC-19 is explicit that a <c>DELETE</c> returning success while
///         flipping a flag would mislead a records-destruction workflow; the audited purge that would
///         satisfy one has no feature and no owner (O-20).
///     </para>
/// </remarks>
public static partial class DeactivateStudent
{
    [LoggerMessage(
        EventId = 1202,
        Level = LogLevel.Information,
        Message = "Student deactivated. StudentId: {StudentId}, SchoolId: {SchoolId}")]
    private static partial void LogStudentDeactivated(ILogger logger, Guid studentId, Guid schoolId);

    public sealed class Command : IRequest
    {
        public required Guid SchoolId { get; init; }

        public required Guid StudentId { get; init; }
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

            Student student = await dbContext.Students
                                  .FirstOrDefaultAsync(
                                      candidate => candidate.Id == request.StudentId
                                                   && candidate.SchoolId == request.SchoolId,
                                      cancellationToken)
                              ?? throw new NotFoundException(ErrorCodes.Student.NotFound);

            // A DELETE commands the transition, so Apply checks the privilege before it consults the
            // row's current state. For a Student that privilege is SchoolScope, which the caller
            // satisfied at EnsureAuthorized, so no 403 is reachable — but the ordering is the shared
            // contract's and is not re-derived here.
            if (!ActivationPolicy.Apply(
                    student,
                    requestedIsActive: false,
                    currentUser,
                    ActivationPrivilege.SchoolScope,
                    nameof(Student)))
            {
                // Already inactive. Saving here would stamp ModifiedAt through the interceptor and
                // report a change that did not happen, making lastUpdatedAt lie.
                return;
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            LogStudentDeactivated(logger, student.Id, student.SchoolId);
        }
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            app.MapDelete("/schools/{schoolId}/students/{studentId}", async (
                    Guid schoolId,
                    Guid studentId,
                    IMediator mediator,
                    CancellationToken cancellationToken) =>
                {
                    await mediator.Send(
                        new Command { SchoolId = schoolId, StudentId = studentId },
                        cancellationToken);

                    return Results.NoContent();
                })
                .WithName(nameof(DeactivateStudent))
                .WithTags("Students")
                .Produces(StatusCodes.Status204NoContent)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
    }
}
