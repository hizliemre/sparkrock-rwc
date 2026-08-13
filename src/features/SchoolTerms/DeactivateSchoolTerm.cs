using Carter;
using domain.Exceptions;
using domain.SchoolTerms;
using domain.Security;
using infra.persistence.sql;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace features.SchoolTerms;

/// <summary>
///     <c>DELETE /schools/{schoolId}/terms/{termId}</c> — deactivates, returns 204, removes nothing.
/// </summary>
/// <remarks>
///     The shared <c>DELETE</c> handler contract (F02 spec), step for step:
///     <list type="number">
///         <item>resolve scope — <c>EnsureAuthorized</c> on the path school</item>
///         <item>load by <b>both</b> ids, so a term outside the addressed school is indistinguishable from absent</item>
///         <item>absent → <see cref="NotFoundException" /> → 404 <c>TERM.NOT_FOUND</c></item>
///         <item>privilege → <see cref="ActivationPolicy.Apply" /> — <see cref="ActivationPrivilege.SchoolScope" />, so no 403 is reachable</item>
///         <item>already inactive → <c>Apply</c> returned false → return, no save, no log, 204</item>
///         <item>otherwise → <c>Apply</c> set <c>IsActive = false</c> → save → 204</item>
///     </list>
///     <para>
///         <b>Never <c>Remove</c>.</b> <see cref="SchoolTerm" /> derives from <c>BaseEntity</c>, and
///         the audit interceptor throws for <c>EntityState.Deleted</c> on anything that is not
///         soft-deletable (DEC-20) — a 500 rather than a delete.
///     </para>
///     <para>
///         Deactivating a term does <b>not</b> rewrite attendance. <c>StudentAttendance.TermId</c> is
///         nullable and already-recorded rows keep pointing at the superseded term (D-03, DEC-19);
///         F06 and F07 resolve a date to a term among <em>active</em> terms only. Deactivation is also
///         the mechanism that frees a term's dates for a replacement, because the overlap probe
///         considers active terms alone.
///     </para>
/// </remarks>
public static partial class DeactivateSchoolTerm
{
    /// <summary>Names the resource in an <see cref="ActivationPolicy" /> failure message.</summary>
    /// <remarks>
    ///     Unreachable while the privilege is <see cref="ActivationPrivilege.SchoolScope" />, and
    ///     shared with <see cref="UpdateSchoolTerm" /> so the two sites cannot disagree if it ever
    ///     stops being unreachable.
    /// </remarks>
    internal const string ResourceName = "School term";

    [LoggerMessage(
        EventId = 1402,
        Level = LogLevel.Information,
        Message = "School term deactivated. SchoolId: {SchoolId}, TermId: {TermId}")]
    private static partial void LogSchoolTermDeactivated(ILogger logger, Guid schoolId, Guid termId);

    public sealed class Command : IRequest
    {
        public required Guid SchoolId { get; init; }

        public required Guid TermId { get; init; }
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

            // Keyed on both ids, so a term of another school is a 404 indistinguishable from an
            // absent one rather than a row this caller can deactivate through the wrong path.
            SchoolTerm term = await dbContext.SchoolTerms
                                 .FirstOrDefaultAsync(
                                     candidate => candidate.Id == request.TermId
                                                  && candidate.SchoolId == request.SchoolId,
                                     cancellationToken)
                             ?? throw new NotFoundException(ErrorCodes.Term.NotFound);

            // Through the shared policy, never a local `if (term.IsActive != false)`. A term is
            // school-scoped data, so SchoolScope names a privilege the caller has already satisfied
            // by passing EnsureAuthorized and no 403 is reachable — but DEC-20 names School,
            // AttendanceCode and Student and not SchoolTerm, so this is an inference. Routing it
            // through the policy anyway means overturning the inference is one argument at two call
            // sites rather than an audit of the feature.
            if (!ActivationPolicy.Apply(
                    term,
                    requestedIsActive: false,
                    currentUser,
                    ActivationPrivilege.SchoolScope,
                    ResourceName))
            {
                // Already inactive. Returning here is what keeps the log line honest: the save itself
                // is a no-op because the change tracker is empty, so without this the handler would
                // report a deactivation that did not happen.
                return;
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            LogSchoolTermDeactivated(logger, request.SchoolId, term.Id);
        }
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            app.MapDelete("/schools/{schoolId}/terms/{termId}", async (
                    Guid schoolId,
                    Guid termId,
                    IMediator mediator,
                    CancellationToken cancellationToken) =>
                {
                    await mediator.Send(
                        new Command { SchoolId = schoolId, TermId = termId }, cancellationToken);

                    return Results.NoContent();
                })
                .WithName(nameof(DeactivateSchoolTerm))
                .WithTags("SchoolTerms")
                .Produces(StatusCodes.Status204NoContent)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
    }
}
