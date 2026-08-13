using Carter;
using domain.Exceptions;
using domain.Security;
using infra.persistence.sql;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AttendanceCodeEntity = domain.AttendanceCodes.AttendanceCode;

namespace features.AttendanceCodes;

/// <summary>
///     <c>DELETE /attendance-codes/{codeId}</c> — deactivates, returns 204, and removes nothing.
/// </summary>
/// <remarks>
///     The shared <c>DELETE</c> handler contract, minus the scope step, because there is no tenant
///     here and 404 can therefore only mean "no such id":
///     <list type="number">
///         <item>load by id</item>
///         <item>absent → <see cref="NotFoundException" /> → 404</item>
///         <item>privilege → <see cref="ActivationPolicy.Apply" /> → <see cref="ForbiddenException" /> → 403</item>
///         <item>already inactive → <c>Apply</c> returned false → return, no save, 204</item>
///         <item>otherwise → <c>Apply</c> set <c>IsActive = false</c> → save → 204</item>
///     </list>
///     <para>
///         <b>Deactivating a code does not free its value.</b> <c>ix_attendance_codes_value</c> is
///         unique and <em>unfiltered</em>, so the row keeps its occupancy of the global five-character
///         namespace forever and a re-<c>POST</c> of the same value is a 409 (F01c §6). The only route
///         back is <c>PUT { "isActive": true }</c> on this row. Nor does it rewrite history: F08 still
///         renders the rows that reference it, and F07 rejects it in a new submission as a 400 field
///         error.
///     </para>
///     <para>
///         <b>Never <c>Remove</c>.</b> <c>AttendanceCode</c> derives from <c>BaseEntity</c>, and the
///         audit interceptor throws <see cref="InvalidOperationException" /> for
///         <c>EntityState.Deleted</c> on anything that is not soft-deletable (DEC-20) — that surfaces
///         as a 500, not as a delete. Real erasure is DEC-19's audited purge, which has no owner yet.
///     </para>
/// </remarks>
public static partial class DeactivateAttendanceCode
{
    [LoggerMessage(
        EventId = 1302,
        Level = LogLevel.Information,
        Message = "Attendance code deactivated. AttendanceCodeId: {AttendanceCodeId}, Value: {Value}")]
    private static partial void LogAttendanceCodeDeactivated(ILogger logger, Guid attendanceCodeId, string value);

    public sealed class Command : IRequest
    {
        public required Guid CodeId { get; init; }
    }

    internal sealed class CommandHandler(
        IDbContext dbContext,
        ICurrentUser currentUser,
        ILogger<CommandHandler> logger)
        : IRequestHandler<Command>
    {
        public async Task Handle(Command request, CancellationToken cancellationToken)
        {
            AttendanceCodeEntity code = await dbContext.AttendanceCodes
                                            .FirstOrDefaultAsync(
                                                candidate => candidate.Id == request.CodeId,
                                                cancellationToken)
                                        ?? throw new NotFoundException(ErrorCodes.AttendanceCode.NotFound);

            // A DELETE commands the transition, so the privilege is checked before the row's current
            // state is consulted. An unprivileged DELETE on an already-inactive code is therefore a
            // 403 rather than an idempotent 204 — deliberate, so the status cannot be read as a state
            // oracle. See ActivationPolicy.
            if (!ActivationPolicy.Apply(
                    code,
                    requestedIsActive: false,
                    currentUser,
                    ActivationPrivilege.SystemAdmin,
                    "Attendance code"))
            {
                // Already inactive. Saving here would stamp ModifiedAt through the interceptor and
                // report a change that did not happen.
                return;
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            LogAttendanceCodeDeactivated(logger, code.Id, code.Value);
        }
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            app.MapDelete("/attendance-codes/{codeId}", async (
                    Guid codeId,
                    IMediator mediator,
                    CancellationToken cancellationToken) =>
                {
                    await mediator.Send(new Command { CodeId = codeId }, cancellationToken);

                    return Results.NoContent();
                })
                .WithName(nameof(DeactivateAttendanceCode))
                .WithTags("AttendanceCodes")
                .Produces(StatusCodes.Status204NoContent)
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
    }
}
