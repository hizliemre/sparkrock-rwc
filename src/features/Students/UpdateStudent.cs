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
///     <c>PUT /schools/{schoolId}/students/{studentId}</c> — a full replace returning 200 and the
///     updated student.
/// </summary>
/// <remarks>
///     <b><c>schoolId</c> is not writable, and there is no transfer.</b> DEC-08 gives <c>Student</c> a
///     single mutable foreign key with no enrolment history; V-13 is the accepted cost, that a
///     back-dated correction for the former school is rejected after a move. Exposing a transfer
///     through <c>PUT</c> would make that cost reachable by an ordinary edit, with no record of when
///     the move happened. DEC-16 adds that access follows <c>Student.SchoolId</c>, so a transfer also
///     silently revokes the former school's access to rows it recorded. How a transfer should work has
///     a real answer — a point-in-time enrolment entity — and it is deliberately deferred, so F05
///     exposes nothing rather than the broken version.
/// </remarks>
public static partial class UpdateStudent
{
    [LoggerMessage(
        EventId = 1201,
        Level = LogLevel.Information,
        Message = "Student updated. StudentId: {StudentId}, SchoolId: {SchoolId}")]
    private static partial void LogStudentUpdated(ILogger logger, Guid studentId, Guid schoolId);

    public sealed class Command : IRequest<GetStudentById.Response>
    {
        /// <summary>From the route, which is authoritative; the body must not repeat it.</summary>
        public required Guid SchoolId { get; init; }

        public required Guid StudentId { get; init; }

        public required string FirstName { get; init; }

        public required string LastName { get; init; }

        /// <summary>
        ///     Optional, and an omitted <c>grade</c> <b>clears</b> it.
        /// </summary>
        /// <remarks>
        ///     Replace semantics. Surprising, documented, and the only way to clear a grade at all —
        ///     treating absent as "keep" makes <c>PUT</c> a partial update.
        /// </remarks>
        public string? Grade { get; init; }

        /// <summary>
        ///     Required on a replace, and nullable so that "absent" is distinguishable from "false".
        /// </summary>
        /// <remarks>
        ///     A non-nullable <c>bool</c> would deserialise an omitted member to <c>false</c>, so every
        ///     partial update sent by a client that did not know about the flag would silently
        ///     deactivate the student. The validator is what rejects the absence, which keeps the
        ///     answer a <c>VALIDATION.REQUIRED_FIELD</c> 400 rather than a binder-level one.
        /// </remarks>
        public bool? IsActive { get; init; }
    }

    /// <summary>
    ///     The JSON payload. <b>No <c>SchoolId</c> member</b> — the route value is authoritative, and
    ///     its absence is what makes a transfer unexpressible rather than merely discouraged.
    /// </summary>
    public sealed class Body
    {
        public required string FirstName { get; init; }

        public required string LastName { get; init; }

        public string? Grade { get; init; }

        public bool? IsActive { get; init; }
    }

    internal sealed class CommandValidator : AbstractValidator<Command>
    {
        public CommandValidator()
        {
            RuleFor(command => command.FirstName)
                .Cascade(CascadeMode.Stop)
                .NotEmpty()
                .WithErrorCode(ErrorCodes.Validation.RequiredField)
                .WithMessage("First name is required.")
                .MaximumLength(CreateStudent.MaxNameLength)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage(FormattableString.Invariant(
                    $"First name must not exceed {CreateStudent.MaxNameLength} characters."));

            RuleFor(command => command.LastName)
                .Cascade(CascadeMode.Stop)
                .NotEmpty()
                .WithErrorCode(ErrorCodes.Validation.RequiredField)
                .WithMessage("Last name is required.")
                .MaximumLength(CreateStudent.MaxNameLength)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage(FormattableString.Invariant(
                    $"Last name must not exceed {CreateStudent.MaxNameLength} characters."));

            RuleFor(command => command.Grade)
                .MaximumLength(CreateStudent.MaxGradeLength)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage(FormattableString.Invariant(
                    $"Grade must not exceed {CreateStudent.MaxGradeLength} characters."));

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
        : IRequestHandler<Command, GetStudentById.Response>
    {
        public async Task<GetStudentById.Response> Handle(Command request, CancellationToken cancellationToken)
        {
            currentUser.EnsureAuthorized(request.SchoolId, ErrorCodes.School.NotFound);

            // Both ids in one predicate, so a student of another school is indistinguishable from an
            // absent one by construction.
            Student student = await dbContext.Students
                                  .FirstOrDefaultAsync(
                                      candidate => candidate.Id == request.StudentId
                                                   && candidate.SchoolId == request.SchoolId,
                                      cancellationToken)
                              ?? throw new NotFoundException(ErrorCodes.Student.NotFound);

            // The whole activation half, and nothing else — no local
            // `if (student.IsActive != request.IsActive)`. The privilege attaches to the transition
            // wherever it occurs (O-12), so DELETE and PUT { isActive: false } reach the same state
            // through the same function. SchoolScope performs no check today — DEC-20 requires school
            // scope and no more for a Student, and the caller has already satisfied it — so no 403 is
            // reachable here. Routing through the policy anyway is the point: if the rule ever gains a
            // privilege, it changes at one argument rather than by auditing the feature.
            ActivationPolicy.ApplyReplacement(
                student,
                request.IsActive!.Value,
                currentUser,
                ActivationPrivilege.SchoolScope,
                nameof(Student));

            student.FirstName = request.FirstName;
            student.LastName = request.LastName;

            // Replace semantics: an absent grade clears it. SchoolId is not assigned here and has no
            // member to assign from.
            student.Grade = request.Grade;

            await dbContext.SaveChangesAsync(cancellationToken);

            LogStudentUpdated(logger, student.Id, student.SchoolId);

            return GetStudentById.ToResponse(student);
        }
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            app.MapPut("/schools/{schoolId}/students/{studentId}", async (
                    Guid schoolId,
                    Guid studentId,
                    Body body,
                    IMediator mediator,
                    CancellationToken cancellationToken) =>
                {
                    GetStudentById.Response response = await mediator.Send(
                        new Command
                        {
                            SchoolId = schoolId,
                            StudentId = studentId,
                            FirstName = body.FirstName,
                            LastName = body.LastName,
                            Grade = body.Grade,
                            IsActive = body.IsActive
                        },
                        cancellationToken);

                    return Results.Ok(response);
                })
                .WithName(nameof(UpdateStudent))
                .WithTags("Students")
                .Produces<GetStudentById.Response>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
    }
}
