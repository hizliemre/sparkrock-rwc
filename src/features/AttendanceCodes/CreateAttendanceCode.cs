using Carter;
using domain.Exceptions;
using domain.Security;
using FluentValidation;
using infra.persistence.sql;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using AttendanceCodeEntity = domain.AttendanceCodes.AttendanceCode;

namespace features.AttendanceCodes;

/// <summary>
///     <c>POST /attendance-codes</c> — 201 with a <c>Location</c> header and the created code.
/// </summary>
/// <remarks>
///     <b>Duplicate values: one insert, no pre-<c>SELECT</c>.</b> A read-then-insert is a TOCTOU with
///     a nicer-looking handler; <c>ix_attendance_codes_value</c> is the only race-free authority, and
///     the <c>23505</c> it raises is translated into a 409
///     <c>ATTENDANCE_CODE.DUPLICATE_VALUE</c> by the constraint registry. The 409 fires
///     <em>regardless of the occupant's <c>IsActive</c></em> — that is the visible consequence of the
///     index being unfiltered, and the only route back is <c>PUT { "isActive": true }</c> on the
///     existing row.
/// </remarks>
public static partial class CreateAttendanceCode
{
    /// <summary>Mirrors <c>attendance_codes.value varchar(5)</c>, and legacy's <c>AttendCode VARCHAR(5)</c>.</summary>
    public const int MaxValueLength = 5;

    /// <summary>Mirrors <c>attendance_codes.description varchar(100)</c>.</summary>
    public const int MaxDescriptionLength = 100;

    [LoggerMessage(
        EventId = 1300,
        Level = LogLevel.Information,
        Message = "Attendance code created. AttendanceCodeId: {AttendanceCodeId}, Value: {Value}")]
    private static partial void LogAttendanceCodeCreated(ILogger logger, Guid attendanceCodeId, string value);

    /// <summary>
    ///     No <c>IsActive</c> member, deliberately.
    /// </summary>
    /// <remarks>
    ///     New codes are active. Accepting the flag here would be a third path to an inactive code and
    ///     the only one that is not a transition, so it would bypass the DEC-20 privilege check that
    ///     <c>ActivationPolicy</c> exists to centralise.
    ///     <para>
    ///         <c>IsAbsent</c> and <c>IsExcused</c> are nullable so that "absent from the body" is
    ///         distinguishable from "false". A non-nullable <c>bool</c> would deserialise an omitted
    ///         member to <c>false</c> and silently create a code meaning the opposite of what the
    ///         caller intended, with no error anywhere — spec §4 makes both required fields.
    ///     </para>
    /// </remarks>
    public sealed class Command : IRequest<GetAttendanceCodeById.Response>
    {
        public required string Value { get; init; }

        public required string Description { get; init; }

        public required bool? IsAbsent { get; init; }

        public required bool? IsExcused { get; init; }
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
                // Against the trimmed value, because that is what gets stored: `"  ABCDE  "` fits
                // varchar(5) once normalised, and MaximumLength on the raw string would reject it.
                .Must(value => value.Trim().Length <= MaxValueLength)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage(FormattableString.Invariant($"Value must not exceed {MaxValueLength} characters."))
                .Must(ContainNoInteriorWhitespace)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage("Value must not contain whitespace.");

            RuleFor(command => command.Description)
                .Cascade(CascadeMode.Stop)
                .NotEmpty()
                .WithErrorCode(ErrorCodes.Validation.RequiredField)
                .WithMessage("Description is required.")
                .MaximumLength(MaxDescriptionLength)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage(FormattableString.Invariant(
                    $"Description must not exceed {MaxDescriptionLength} characters."));

            RuleFor(command => command.IsAbsent)
                .NotNull()
                .WithErrorCode(ErrorCodes.Validation.RequiredField)
                .WithMessage("Is absent is required.");

            RuleFor(command => command.IsExcused)
                .NotNull()
                .WithErrorCode(ErrorCodes.Validation.RequiredField)
                .WithMessage("Is excused is required.");
        }

        /// <summary>
        ///     Surrounding whitespace is normalised away, so only interior whitespace is a failure.
        /// </summary>
        /// <remarks>
        ///     A code containing a space would be storable but unusable: F07 resolves codes by value
        ///     from a JSON body, where a trailing or interior space is invisible in every client that
        ///     renders it.
        /// </remarks>
        internal static bool ContainNoInteriorWhitespace(string value) =>
            !value.Trim().Any(char.IsWhiteSpace);
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
            // Creating a code is unscoped by design — the aggregate has no tenant — so the only
            // available check is privilege, and it runs before anything is constructed. DEC-20 names
            // only deactivation; extending it to creation is an inference, recorded as one in spec §4.
            // The reasoning is that Value is unique unfiltered: a created code occupies its value
            // permanently, across every school, and nothing can free it.
            if (!currentUser.IsSystemAdmin)
            {
                throw new ForbiddenException(
                    ErrorCodes.System.Forbidden,
                    "Attendance codes may only be created by a system administrator.");
            }

            AttendanceCodeEntity code = new()
            {
                Value = AttendanceCodeValue.Normalise(request.Value),
                Description = request.Description,

                // The validator guarantees both are present; a handler-tier caller that omits one is
                // a programming error rather than a request.
                IsAbsent = request.IsAbsent!.Value,
                IsExcused = request.IsExcused!.Value
            };

            dbContext.AttendanceCodes.Add(code);
            await dbContext.SaveChangesAsync(cancellationToken);

            // Once, after the save. A code value is a bounded, non-PII token (conventions §4).
            LogAttendanceCodeCreated(logger, code.Id, code.Value);

            return GetAttendanceCodeById.ToResponse(code);
        }
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            app.MapPost("/attendance-codes", async (
                    Command command,
                    IMediator mediator,
                    CancellationToken cancellationToken) =>
                {
                    GetAttendanceCodeById.Response response = await mediator.Send(command, cancellationToken);

                    return Results.Created($"/api/v1/attendance-codes/{response.Id}", response);
                })
                .WithName(nameof(CreateAttendanceCode))
                .WithTags("AttendanceCodes")
                .Produces<GetAttendanceCodeById.Response>(StatusCodes.Status201Created)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }
    }
}
