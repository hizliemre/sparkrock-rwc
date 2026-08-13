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
using Microsoft.Extensions.Logging;

namespace features.Schools;

/// <summary>
///     <c>POST /schools</c> — 201 with a <c>Location</c> header and the created school.
/// </summary>
public static partial class CreateSchool
{
    /// <summary>Mirrors <c>schools.name varchar(200)</c>.</summary>
    public const int MaxNameLength = 200;

    /// <summary>Mirrors <c>schools.time_zone_id varchar(64)</c>.</summary>
    public const int MaxTimeZoneIdLength = 64;

    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Information,
        Message = "School created. SchoolId: {SchoolId}")]
    private static partial void LogSchoolCreated(ILogger logger, Guid schoolId);

    /// <summary>
    ///     No <c>IsActive</c> member, deliberately.
    /// </summary>
    /// <remarks>
    ///     New schools are active. Accepting the flag here would be a third path to an inactive school
    ///     and the only one that is not a transition, so it would bypass the DEC-20 privilege check
    ///     that <c>ActivationPolicy</c> exists to centralise.
    /// </remarks>
    public sealed class Command : IRequest<GetSchoolById.Response>
    {
        public required string Name { get; init; }

        public required string TimeZoneId { get; init; }

        /// <summary>Optional. Absent means "use the domain default" (V-26).</summary>
        public int? AbsenceAlertThreshold { get; init; }
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
                .MaximumLength(MaxNameLength)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage(FormattableString.Invariant($"Name must not exceed {MaxNameLength} characters."));

            RuleFor(command => command.TimeZoneId)
                .Cascade(CascadeMode.Stop)
                .NotEmpty()
                .WithErrorCode(ErrorCodes.Validation.RequiredField)
                .WithMessage("Time zone id is required.")
                .MaximumLength(MaxTimeZoneIdLength)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage(FormattableString.Invariant(
                    $"Time zone id must not exceed {MaxTimeZoneIdLength} characters."))
                .Must(BeAResolvableTimeZone)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage("Time zone id is not a known time zone.");

            // Mirrors ck_schools_absence_alert_threshold_positive. The constraint is the real guard; a
            // validator that allowed a value the column rejects turns a field error into a 500.
            RuleFor(command => command.AbsenceAlertThreshold)
                .GreaterThan(0)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage("Absence alert threshold must be greater than zero.")
                .When(command => command.AbsenceAlertThreshold.HasValue);
        }

        /// <summary>
        ///     A validator rule, and therefore a 400, rather than an exception at write time.
        /// </summary>
        /// <remarks>
        ///     <c>TryFindSystemTimeZoneById</c> rather than the throwing overload: an unresolvable zone
        ///     is an ordinary field error and paying for an exception to discover that is wrong on the
        ///     path a bulk import takes.
        /// </remarks>
        private static bool BeAResolvableTimeZone(string timeZoneId) =>
            TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out TimeZoneInfo? _);
    }

    internal sealed class CommandHandler(
        IDbContext dbContext,
        ICurrentUser currentUser,
        ILogger<CommandHandler> logger)
        : IRequestHandler<Command, GetSchoolById.Response>
    {
        public async Task<GetSchoolById.Response> Handle(Command request, CancellationToken cancellationToken)
        {
            // Creating a school is unscoped by design — it creates the scope key — so the only
            // available check is privilege. DEC-20 does not name creation; this is an inference,
            // recorded as one in the spec, and it is the check that moves if a school-administrator
            // role later appears.
            if (!currentUser.IsSystemAdmin)
            {
                throw new ForbiddenException(
                    ErrorCodes.System.Forbidden,
                    "Schools may only be created by a system administrator.");
            }

            School school = new()
            {
                Name = request.Name,
                TimeZoneId = request.TimeZoneId,
                AbsenceAlertThreshold = request.AbsenceAlertThreshold
            };

            dbContext.Schools.Add(school);
            await dbContext.SaveChangesAsync(cancellationToken);

            // Once, after the save, with the id only. No name, no PII (conventions §4).
            LogSchoolCreated(logger, school.Id);

            return GetSchoolById.ToResponse(school);
        }
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            app.MapPost("/schools", async (
                    Command command,
                    IMediator mediator,
                    CancellationToken cancellationToken) =>
                {
                    GetSchoolById.Response response = await mediator.Send(command, cancellationToken);

                    return Results.Created($"/api/v1/schools/{response.Id}", response);
                })
                .WithName(nameof(CreateSchool))
                .WithTags("Schools")
                .Produces<GetSchoolById.Response>(StatusCodes.Status201Created)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status403Forbidden);
        }
    }
}
