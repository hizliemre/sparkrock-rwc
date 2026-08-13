using Carter;
using domain.Exceptions;
using domain.Security;
using FluentValidation;
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
///     <c>PUT /attendance-codes/{codeId}</c> — a full replace returning 200 and the updated code.
/// </summary>
/// <remarks>
///     <b><c>Value</c> is immutable, and the body carries it anyway.</b> A changed value would orphan
///     the text already snapshotted into <c>StudentAttendance.AttendCode</c> (D-02, V-23) and would
///     move an occupancy in the unfiltered unique namespace. Omitting <c>value</c> from the request
///     model is worse than rejecting a mismatch: unmatched JSON members are ignored by default, so a
///     client sending <c>{"value": "B", …}</c> would get a 200 and no change. An explicit rejection is
///     the only version a client can act on.
///     <para>
///         Handler order is load → 404 → privilege → value immutability → activation → assign → save.
///         The privilege check precedes the value check so an unprivileged caller cannot probe stored
///         values through the difference between a 400 and a 200, and it follows the load so the
///         status is not an existence oracle in the other direction.
///     </para>
/// </remarks>
public static partial class UpdateAttendanceCode
{
    [LoggerMessage(
        EventId = 1301,
        Level = LogLevel.Information,
        Message = "Attendance code updated. AttendanceCodeId: {AttendanceCodeId}, Value: {Value}")]
    private static partial void LogAttendanceCodeUpdated(ILogger logger, Guid attendanceCodeId, string value);

    public sealed class Command : IRequest<GetAttendanceCodeById.Response>
    {
        /// <summary>From the route, which is authoritative; the body must not repeat it.</summary>
        public required Guid CodeId { get; init; }

        /// <summary>
        ///     Must match the stored value once normalised, or the request is rejected.
        /// </summary>
        /// <remarks>
        ///     Comparison is against the normalised form, so <c>"a"</c> matches a stored <c>A</c> —
        ///     a client that round-trips a <c>GET</c> through a case-mangling form field is not
        ///     attempting a rename.
        /// </remarks>
        public required string Value { get; init; }

        public required string Description { get; init; }

        public required bool? IsAbsent { get; init; }

        public required bool? IsExcused { get; init; }

        /// <summary>
        ///     Required on a replace, and nullable so that "absent" is distinguishable from "false".
        /// </summary>
        /// <remarks>
        ///     A non-nullable <c>bool</c> would deserialise an omitted member to <c>false</c>, so every
        ///     partial update sent by a client that did not know about the flag would silently
        ///     deactivate the code. The validator is what rejects the absence, which keeps the answer a
        ///     <c>VALIDATION.REQUIRED_FIELD</c> 400 rather than a binder-level malformed-request one.
        /// </remarks>
        public required bool? IsActive { get; init; }
    }

    /// <summary>
    ///     The JSON payload. Separate from <see cref="Command" /> because the command carries the route
    ///     value and conventions §2 forbids a body repeating <c>codeId</c>.
    /// </summary>
    public sealed class Body
    {
        public required string Value { get; init; }

        public required string Description { get; init; }

        public required bool? IsAbsent { get; init; }

        public required bool? IsExcused { get; init; }

        public required bool? IsActive { get; init; }
    }

    internal sealed class CommandValidator : AbstractValidator<Command>
    {
        public CommandValidator()
        {
            RuleFor(command => command.Value)
                .Cascade(CascadeMode.Stop)
                .NotEmpty()
                .WithErrorCode(ErrorCodes.Validation.RequiredField)
                .WithMessage("Value is required.")
                .Must(value => value.Trim().Length <= CreateAttendanceCode.MaxValueLength)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage(FormattableString.Invariant(
                    $"Value must not exceed {CreateAttendanceCode.MaxValueLength} characters."))
                .Must(CreateAttendanceCode.CommandValidator.ContainNoInteriorWhitespace)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage("Value must not contain whitespace.");

            RuleFor(command => command.Description)
                .Cascade(CascadeMode.Stop)
                .NotEmpty()
                .WithErrorCode(ErrorCodes.Validation.RequiredField)
                .WithMessage("Description is required.")
                .MaximumLength(CreateAttendanceCode.MaxDescriptionLength)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage(FormattableString.Invariant(
                    $"Description must not exceed {CreateAttendanceCode.MaxDescriptionLength} characters."));

            RuleFor(command => command.IsAbsent)
                .NotNull()
                .WithErrorCode(ErrorCodes.Validation.RequiredField)
                .WithMessage("Is absent is required.");

            RuleFor(command => command.IsExcused)
                .NotNull()
                .WithErrorCode(ErrorCodes.Validation.RequiredField)
                .WithMessage("Is excused is required.");

            RuleFor(command => command.IsActive)
                .NotNull()
                .WithErrorCode(ErrorCodes.Validation.RequiredField)
                .WithMessage("Is active is required on a replace.");
        }
    }

    internal sealed class CommandHandler(
        IDbContext dbContext,
        ICurrentUser currentUser,
        ILogger<CommandHandler> logger)
        : IRequestHandler<Command, GetAttendanceCodeById.Response>
    {
        public async Task<GetAttendanceCodeById.Response> Handle(
            Command request,
            CancellationToken cancellationToken)
        {
            // No EnsureAuthorized and no scope filter: the aggregate is global (O-03), so 404 can only
            // mean "no such id" and never "not yours".
            AttendanceCodeEntity code = await dbContext.AttendanceCodes
                                            .FirstOrDefaultAsync(
                                                candidate => candidate.Id == request.CodeId,
                                                cancellationToken)
                                        ?? throw new NotFoundException(ErrorCodes.AttendanceCode.NotFound);

            // Every write to this aggregate is admin-only, not only the activation transition — spec
            // §4's inference from the value namespace being global and permanently occupied. This runs
            // before the value check so the 400/200 difference cannot be used to probe stored values,
            // and after the load so the 403/404 difference cannot be used to probe existence.
            if (!currentUser.IsSystemAdmin)
            {
                throw new ForbiddenException(
                    ErrorCodes.System.Forbidden,
                    "Attendance codes may only be changed by a system administrator.");
            }

            string requestedValue = AttendanceCodeValue.Normalise(request.Value);

            if (!string.Equals(requestedValue, code.Value, StringComparison.Ordinal))
            {
                // A single-violation BusinessRuleException, which conventions §2 permits: violations
                // is present iff the failure is per-item. The message names neither the stored value
                // nor the rejected one — api's ViolationMessage redacts unbounded values anyway, and
                // the stored value is the thing a probe would be after.
                throw new BusinessRuleException(
                    ErrorCodes.AttendanceCode.ValueImmutable,
                    [
                        new Violation(
                            "body",
                            nameof(Command.Value),
                            ErrorCodes.AttendanceCode.ValueImmutable,
                            "Value is immutable and must match the stored code.")
                    ]);
            }

            // The whole activation half, and nothing else — no local
            // `if (code.IsActive != request.IsActive)`. The privilege attaches to the transition
            // wherever it occurs (O-12), so DELETE and PUT { isActive: false } fail in the same place.
            // The admin check above has already refused every non-admin here, so this call's own
            // refusal is unreachable through the endpoint; it stays because removing it would move the
            // rule back into the slice, and because a future editor role would change the check above
            // and not this one.
            ActivationPolicy.ApplyReplacement(
                code,
                request.IsActive!.Value,
                currentUser,
                ActivationPrivilege.SystemAdmin,
                "Attendance code");

            code.Description = request.Description;
            code.IsAbsent = request.IsAbsent!.Value;
            code.IsExcused = request.IsExcused!.Value;

            await dbContext.SaveChangesAsync(cancellationToken);

            LogAttendanceCodeUpdated(logger, code.Id, code.Value);

            return GetAttendanceCodeById.ToResponse(code);
        }
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            app.MapPut("/attendance-codes/{codeId}", async (
                    Guid codeId,
                    Body body,
                    IMediator mediator,
                    CancellationToken cancellationToken) =>
                {
                    GetAttendanceCodeById.Response response = await mediator.Send(
                        new Command
                        {
                            CodeId = codeId,
                            Value = body.Value,
                            Description = body.Description,
                            IsAbsent = body.IsAbsent,
                            IsExcused = body.IsExcused,
                            IsActive = body.IsActive
                        },
                        cancellationToken);

                    return Results.Ok(response);
                })
                .WithName(nameof(UpdateAttendanceCode))
                .WithTags("AttendanceCodes")
                .Produces<GetAttendanceCodeById.Response>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
    }
}
