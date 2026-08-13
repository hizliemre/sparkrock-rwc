using Carter;
using domain.Exceptions;
using domain.Schools;
using domain.Security;
using FluentValidation;
using infra.persistence.sql;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace features.Schools;

/// <summary>
///     <c>PUT /schools/{schoolId}</c> — a full replace returning 200 and the updated school.
/// </summary>
public static partial class UpdateSchool
{
    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Information,
        Message = "School updated. SchoolId: {SchoolId}")]
    private static partial void LogSchoolUpdated(ILogger logger, Guid schoolId);

    public sealed class Command : IRequest<GetSchoolById.Response>
    {
        /// <summary>From the route, which is authoritative; the body must not repeat it.</summary>
        public required Guid SchoolId { get; init; }

        public required string Name { get; init; }

        public required string TimeZoneId { get; init; }

        public int? AbsenceAlertThreshold { get; init; }

        /// <summary>
        ///     Required on a replace, and nullable so that "absent" is distinguishable from "false".
        /// </summary>
        /// <remarks>
        ///     A non-nullable <c>bool</c> would deserialise an omitted member to <c>false</c>, so every
        ///     partial update sent by a client that did not know about the flag would silently
        ///     deactivate the school. The validator is what rejects the absence, which keeps the answer
        ///     a <c>VALIDATION.REQUIRED_FIELD</c> 400 rather than a binder-level malformed-request one.
        /// </remarks>
        public bool? IsActive { get; init; }
    }

    /// <summary>
    ///     The JSON payload. Separate from <see cref="Command" /> because the command carries the route
    ///     value and conventions §2 forbids a body repeating <c>schoolId</c>.
    /// </summary>
    public sealed class Body
    {
        public required string Name { get; init; }

        public required string TimeZoneId { get; init; }

        public int? AbsenceAlertThreshold { get; init; }

        public bool? IsActive { get; init; }
    }

    internal sealed class CommandValidator : AbstractValidator<Command>
    {
        public CommandValidator()
        {
            RuleFor(command => command.Name)
                .Cascade(CascadeMode.Stop)
                .NotEmpty()
                .WithErrorCode(ErrorCodes.Validation.RequiredField)
                .WithMessage("Name is required.")
                .MaximumLength(CreateSchool.MaxNameLength)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage(FormattableString.Invariant(
                    $"Name must not exceed {CreateSchool.MaxNameLength} characters."));

            RuleFor(command => command.TimeZoneId)
                .Cascade(CascadeMode.Stop)
                .NotEmpty()
                .WithErrorCode(ErrorCodes.Validation.RequiredField)
                .WithMessage("Time zone id is required.")
                .MaximumLength(CreateSchool.MaxTimeZoneIdLength)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage(FormattableString.Invariant(
                    $"Time zone id must not exceed {CreateSchool.MaxTimeZoneIdLength} characters."))
                .Must(timeZoneId => TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out TimeZoneInfo? _))
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage("Time zone id is not a known time zone.");

            RuleFor(command => command.AbsenceAlertThreshold)
                .GreaterThan(0)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage("Absence alert threshold must be greater than zero.")
                .When(command => command.AbsenceAlertThreshold.HasValue);

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
        : IRequestHandler<Command, GetSchoolById.Response>
    {
        public async Task<GetSchoolById.Response> Handle(Command request, CancellationToken cancellationToken)
        {
            // Scope, then existence, then privilege. Reversing the first two answers 403 for a school
            // the caller cannot see, which is an existence oracle.
            currentUser.EnsureAuthorized(request.SchoolId, ErrorCodes.School.NotFound);

            School school = await dbContext.Schools
                                .FirstOrDefaultAsync(candidate => candidate.Id == request.SchoolId, cancellationToken)
                            ?? throw new NotFoundException(ErrorCodes.School.NotFound);

            // The whole activation half, and nothing else — no local
            // `if (school.IsActive != request.IsActive)`. The privilege attaches to the transition
            // wherever it occurs (O-12), so DELETE and PUT { isActive: false } fail in the same place.
            // The replace form is what a PUT means: a body restating the current flag is not a
            // transition, so it does not need the privilege, and the comparison that decides this
            // lives in the policy rather than here. The validator guarantees IsActive is present; a
            // handler-tier caller that omits it is a programming error rather than a request.
            ActivationPolicy.ApplyReplacement(
                school,
                request.IsActive!.Value,
                currentUser,
                ActivationPrivilege.SystemAdmin,
                nameof(School));

            school.Name = request.Name;
            school.TimeZoneId = request.TimeZoneId;
            school.AbsenceAlertThreshold = request.AbsenceAlertThreshold;

            await dbContext.SaveChangesAsync(cancellationToken);

            LogSchoolUpdated(logger, school.Id);

            return GetSchoolById.ToResponse(school);
        }
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            app.MapPut("/schools/{schoolId}", async (
                    Guid schoolId,
                    Body body,
                    IMediator mediator,
                    CancellationToken cancellationToken) =>
                {
                    GetSchoolById.Response response = await mediator.Send(
                        new Command
                        {
                            SchoolId = schoolId,
                            Name = body.Name,
                            TimeZoneId = body.TimeZoneId,
                            AbsenceAlertThreshold = body.AbsenceAlertThreshold,
                            IsActive = body.IsActive
                        },
                        cancellationToken);

                    return Results.Ok(response);
                })
                .WithName(nameof(UpdateSchool))
                .WithTags("Schools")
                .Produces<GetSchoolById.Response>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
    }
}
