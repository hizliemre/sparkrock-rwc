using Carter;
using domain.Exceptions;
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

namespace features.Students;

/// <summary>
///     <c>POST /schools/{schoolId}/students</c> — 201 with a <c>Location</c> header and the created
///     student.
/// </summary>
public static partial class CreateStudent
{
    /// <summary>Mirrors <c>students.first_name</c> and <c>students.last_name</c>, both <c>varchar(100)</c>.</summary>
    public const int MaxNameLength = 100;

    /// <summary>Mirrors <c>students.grade varchar(10)</c>.</summary>
    public const int MaxGradeLength = 10;

    [LoggerMessage(
        EventId = 1200,
        Level = LogLevel.Information,
        Message = "Student created. StudentId: {StudentId}, SchoolId: {SchoolId}")]
    private static partial void LogStudentCreated(ILogger logger, Guid studentId, Guid schoolId);

    /// <summary>
    ///     No <c>IsActive</c> member, deliberately.
    /// </summary>
    /// <remarks>
    ///     New students are active. Accepting the flag here would be a path to an inactive student
    ///     that is not a transition, so it would bypass <c>ActivationPolicy</c> — the single home of
    ///     the DEC-20 rule — entirely (O-12).
    /// </remarks>
    public sealed class Command : IRequest<GetStudentById.Response>
    {
        /// <summary>From the route, which is authoritative; the body must not repeat it.</summary>
        public required Guid SchoolId { get; init; }

        public required string FirstName { get; init; }

        public required string LastName { get; init; }

        /// <summary>Optional, matching the nullable column. Absent means "no grade recorded".</summary>
        public string? Grade { get; init; }
    }

    /// <summary>
    ///     The JSON payload. Separate from <see cref="Command" /> because the command carries the route
    ///     value and conventions §2 forbids a body repeating <c>schoolId</c>.
    /// </summary>
    public sealed class Body
    {
        public required string FirstName { get; init; }

        public required string LastName { get; init; }

        public string? Grade { get; init; }
    }

    internal sealed class CommandValidator : AbstractValidator<Command>
    {
        public CommandValidator()
        {
            // No format rule on names — no regex, no character class. Names carry apostrophes,
            // hyphens, spaces, diacritics and scripts a validator author will not anticipate, and a
            // rejected legitimate name is a child who cannot be marked present.
            RuleFor(command => command.FirstName)
                .Cascade(CascadeMode.Stop)
                .NotEmpty()
                .WithErrorCode(ErrorCodes.Validation.RequiredField)
                .WithMessage("First name is required.")
                .MaximumLength(MaxNameLength)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage(FormattableString.Invariant(
                    $"First name must not exceed {MaxNameLength} characters."));

            RuleFor(command => command.LastName)
                .Cascade(CascadeMode.Stop)
                .NotEmpty()
                .WithErrorCode(ErrorCodes.Validation.RequiredField)
                .WithMessage("Last name is required.")
                .MaximumLength(MaxNameLength)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage(FormattableString.Invariant(
                    $"Last name must not exceed {MaxNameLength} characters."));

            RuleFor(command => command.Grade)
                .MaximumLength(MaxGradeLength)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage(FormattableString.Invariant($"Grade must not exceed {MaxGradeLength} characters."));
        }
    }

    internal sealed class CommandHandler(
        IDbContext dbContext,
        ICurrentUser currentUser,
        ILogger<CommandHandler> logger)
        : IRequestHandler<Command, GetStudentById.Response>
    {
        public async Task<GetStudentById.Response> Handle(Command request, CancellationToken cancellationToken)
        {
            // Scope, then existence. School membership is the whole check — F05 has no 403 anywhere.
            currentUser.EnsureAuthorized(request.SchoolId, ErrorCodes.School.NotFound);

            // Before the insert, so STUDENT.REFERENCE_MISSING — the foreign key's translation — stays
            // unreachable outside a race, and a path {id} that does not resolve is a 404 rather than a
            // 500 from the constraint.
            bool schoolExists = await dbContext.Schools
                .AsNoTracking()
                .AnyAsync(school => school.Id == request.SchoolId, cancellationToken);

            if (!schoolExists)
                throw new NotFoundException(ErrorCodes.School.NotFound);

            // No uniqueness on names or on (school, name). F01c declined it: a real school has two
            // students with the same name, and inventing a natural key makes the import reject
            // legitimate rows.
            Student student = new()
            {
                SchoolId = request.SchoolId,
                FirstName = request.FirstName,
                LastName = request.LastName,
                Grade = request.Grade
            };

            dbContext.Students.Add(student);
            await dbContext.SaveChangesAsync(cancellationToken);

            // Once, after the save, with ids only. Never a name and never a grade: conventions §4 was
            // written for this feature, and a name in a log line survives log retention.
            LogStudentCreated(logger, student.Id, student.SchoolId);

            return GetStudentById.ToResponse(student);
        }
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            app.MapPost("/schools/{schoolId}/students", async (
                    Guid schoolId,
                    Body body,
                    IMediator mediator,
                    CancellationToken cancellationToken) =>
                {
                    GetStudentById.Response response = await mediator.Send(
                        new Command
                        {
                            SchoolId = schoolId,
                            FirstName = body.FirstName,
                            LastName = body.LastName,
                            Grade = body.Grade
                        },
                        cancellationToken);

                    return Results.Created(
                        FormattableString.Invariant($"/api/v1/schools/{schoolId}/students/{response.Id}"),
                        response);
                })
                .WithName(nameof(CreateStudent))
                .WithTags("Students")
                .Produces<GetStudentById.Response>(StatusCodes.Status201Created)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
    }
}
