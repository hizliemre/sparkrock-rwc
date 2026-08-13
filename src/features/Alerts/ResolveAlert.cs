using Carter;
using domain.Alerts;
using domain.Exceptions;
using domain.Schools;
using domain.Security;
using domain.Students;
using FluentValidation;
using infra.persistence.sql;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace features.Alerts;

/// <summary>
///     <c>POST /alerts/{alertId}/resolution</c> — closes one episode by hand, and returns
///     <b>200</b> with the updated alert.
/// </summary>
/// <remarks>
///     Three things about that contract are decided rather than defaulted (O-02):
///     <list type="bullet">
///         <item>
///             <b>Singular <c>resolution</c>.</b> The plural names a collection whose members are
///             addressable; DEC-18 models a re-raise as a <em>new episode row</em>, not a second
///             resolution of the same episode, so the collection would always hold zero or one member
///             and neither would have a URL.
///         </item>
///         <item>
///             <b><c>POST</c>, not <c>PUT</c>.</b> A second resolution of a resolved alert is a 409;
///             answering 409 to a verb that promises idempotent replacement contradicts the verb.
///         </item>
///         <item>
///             <b>200 with the alert, no <c>Location</c>.</b> Nothing is created — DEC-18 puts the
///             resolution on the alert's own columns — so there is no id to mint and no target to
///             point at, and a 204 would force a second round trip to a list route that has just
///             changed underneath the client.
///         </item>
///     </list>
///     <para>
///         Scope follows the student's <b>current</b> school (DEC-16), never <c>alert.SchoolId</c>.
///         The receiving school inherits and can close a prior school's episode; the former school
///         cannot. That is not a convenience — F07's auto-resolve evaluates
///         <c>(StudentId, AlertType, SchoolYearStart, SchoolId)</c> against the <em>submitting</em>
///         school, so after a transfer nothing on the write path can ever reach the prior school's
///         episode. If this route did not reach it either, the row would stay open forever: L-07
///         reinstated by construction.
///     </para>
///     <para>
///         <b>No optimistic-concurrency token</b> — F01d gave one to <c>StudentAttendanceSummary</c>
///         only, and adding one is a migration F10 may not author. Two clerks resolving the same open
///         alert simultaneously is a read-then-write race: both pass the 409 check, both write, and
///         the later resolver's reason and identity survive. The episode is closed exactly once and
///         the unique index is not involved (it filters <c>resolved_at IS NULL</c>), so nothing is
///         corrupted — but the audit record names one of two people who both believe they resolved
///         it. Risk R-1, accepted.
///     </para>
/// </remarks>
public static partial class ResolveAlert
{
    /// <summary>
    ///     Mirrors <c>resolution_reason varchar(500)</c> exactly (DEC-06). Postgres <c>text</c> would
    ///     silently accept more; the validator is what makes the bound a 400 rather than a 500.
    /// </summary>
    public const int MaxReasonLength = 500;

    /// <summary>EventId 1600, from the Alerts range 1600–1699 (conventions §4).</summary>
    internal const int AlertResolvedEventId = 1600;

    /// <summary>
    ///     The one log line on this path, written once after <c>SaveChangesAsync</c>.
    /// </summary>
    /// <remarks>
    ///     The template names no student, no name and no reason. <c>ResolutionReason</c> is free text
    ///     written by a human about a safeguarding decision, and conventions §4's ban covers every
    ///     channel that carries a payload the caller did not ask for — a log line read by whoever
    ///     holds log access is exactly one.
    /// </remarks>
    [LoggerMessage(
        EventId = AlertResolvedEventId,
        Level = LogLevel.Information,
        Message = "Alert {AlertId} resolved manually at school {SchoolId}.")]
    private static partial void LogAlertResolved(ILogger logger, Guid alertId, Guid schoolId);

    public sealed class Command : IRequest<GetSchoolAlerts.Response>
    {
        /// <summary>From the route. Route values are authoritative; a body repeating it is ignored.</summary>
        public required Guid AlertId { get; init; }

        /// <summary>
        ///     Why the episode is being closed. Required.
        /// </summary>
        /// <remarks>
        ///     The column is nullable because <em>auto</em>-resolutions write no reason (F07), but a
        ///     manual resolution permanently suppresses re-raising for that student, type, year and
        ///     school for the rest of the school year (DEC-18, <c>AlertRules.ShouldRaise</c>). An
        ///     unexplained permanent suppression of a safeguarding signal is not a state this API will
        ///     create.
        ///     <para>
        ///         There is deliberately <b>no</b> <c>ResolutionSource</c> member.
        ///         <c>AutoBelowThreshold</c> is written only by F07's recount, and accepting the value
        ///         here would let a client disguise a human decision as an automatic one — which is
        ///         precisely the distinction the column exists to preserve. Asserted by
        ///         <c>Command_DeclaresNoResolutionSourceMember</c>.
        ///     </para>
        /// </remarks>
        public required string Reason { get; init; }
    }

    internal sealed class CommandValidator : AbstractValidator<Command>
    {
        public CommandValidator()
        {
            RuleFor(command => command.Reason)
                .NotEmpty()
                .WithErrorCode(ErrorCodes.Validation.RequiredField)
                .WithMessage("A resolution reason is required.")
                .MaximumLength(MaxReasonLength)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage(FormattableString.Invariant(
                    $"Resolution reason must not exceed {MaxReasonLength} characters."));
        }
    }

    internal sealed class CommandHandler(
        IDbContext dbContext,
        ICurrentUser currentUser,
        TimeProvider timeProvider,
        ILogger<CommandHandler> logger)
        : IRequestHandler<Command, GetSchoolAlerts.Response>
    {
        public async Task<GetSchoolAlerts.Response> Handle(Command request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            // Tracked: this row is about to be modified. The reflective soft-delete filter applies
            // (DEC-20), so a soft-deleted alert is absent here rather than resolvable.
            StudentAlert alert = await dbContext.StudentAlerts
                                     .FirstOrDefaultAsync(
                                         candidate => candidate.Id == request.AlertId, cancellationToken)
                                 ?? throw new NotFoundException(ErrorCodes.Alert.NotFound);

            Student student = await dbContext.Students
                                  .AsNoTracking()
                                  .FirstOrDefaultAsync(
                                      candidate => candidate.Id == alert.StudentId, cancellationToken)
                              ?? throw new NotFoundException(ErrorCodes.Alert.NotFound);

            // DEC-16: the student's *current* school, not alert.SchoolId. An unknown id, a
            // soft-deleted alert and an alert outside scope all raise the same exception with the
            // same code and no message — NotFoundException takes no message parameter, so the three
            // are indistinguishable by construction (conventions §2, the existence-oracle rule).
            currentUser.EnsureAuthorized(student.SchoolId, ErrorCodes.Alert.NotFound);

            if (alert.ResolvedAt is not null)
            {
                // 409, never an overwrite. Silently replacing the first resolver's identity and
                // reason would discard the only audit record of a decision that keeps a safeguarding
                // signal switched off for the rest of the school year.
                throw new ConflictException(
                    ErrorCodes.Alert.AlreadyResolved, "This alert has already been resolved.");
            }

            School school = await dbContext.Schools
                                .AsNoTracking()
                                .FirstOrDefaultAsync(
                                    candidate => candidate.Id == student.SchoolId, cancellationToken)
                            ?? throw new NotFoundException(ErrorCodes.Alert.NotFound);

            // All four together. ck_student_alerts_resolution_consistent makes a half-written
            // resolution unrepresentable; this cannot violate it rather than relying on it.
            // ResolutionSource is Manual by construction and is never taken from the request.
            alert.ResolvedAt = timeProvider.GetUtcNow();
            alert.ResolvedBy = currentUser.UserId;
            alert.ResolutionSource = ResolutionSource.Manual;
            alert.ResolutionReason = request.Reason;

            // ModifiedAt/ModifiedBy are the interceptor's (DEC-21) and are not assigned here.
            await dbContext.SaveChangesAsync(cancellationToken);

            LogAlertResolved(logger, alert.Id, student.SchoolId);

            // Built from the entities already in hand rather than by re-issuing the list query.
            // ToRow_MatchesTheProjectedRow asserts the two constructions agree.
            return GetSchoolAlerts.ToResponse(GetSchoolAlerts.ToRow(alert, student, school));
        }
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            // Module-relative, and `resolution` singular. 200, not 201, and no Location header: a
            // Location here would have to point at the alert, which is not what was created, because
            // nothing was created (O-02).
            app.MapPost("/alerts/{alertId:guid}/resolution", async (
                    Guid alertId,
                    Request request,
                    IMediator mediator,
                    CancellationToken cancellationToken) =>
                {
                    GetSchoolAlerts.Response response = await mediator.Send(
                        new Command
                        {
                            AlertId = alertId,

                            // A missing body reaches the validator as a blank reason and becomes a
                            // 400 VALIDATION.REQUIRED_FIELD, rather than a 500 here.
                            Reason = request?.Reason ?? string.Empty
                        },
                        cancellationToken);

                    return Results.Ok(response);
                })
                .WithName(nameof(ResolveAlert))
                .WithTags("Alerts")
                .Produces<GetSchoolAlerts.Response>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }
    }

    /// <summary>
    ///     The request body: <c>reason</c> and nothing else.
    /// </summary>
    /// <remarks>
    ///     A separate type from <see cref="Command" /> because the command carries <c>AlertId</c> from
    ///     the route and a body must not repeat it (conventions §2). <c>Reason</c> is nullable here so
    ///     an omitted member is a validator failure rather than a binding failure — the codes spec §7
    ///     documents are the validator's.
    /// </remarks>
    public sealed class Request
    {
        public string? Reason { get; init; }
    }
}
