using Carter;
using domain.Exceptions;
using domain.SchoolTerms;
using domain.Security;
using FluentValidation;
using infra.persistence.sql;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace features.SchoolTerms;

/// <summary>
///     <c>PUT /schools/{schoolId}/terms/{termId}</c> — a full replace returning 200 and the updated
///     term.
/// </summary>
/// <remarks>
///     The only slice in the feature where two rules interact: a date change and an activation change
///     can arrive in the same request. Both are resolved by probing the <b>resulting</b> state.
/// </remarks>
public static partial class UpdateSchoolTerm
{
    [LoggerMessage(
        EventId = 1401,
        Level = LogLevel.Information,
        Message = "School term updated. SchoolId: {SchoolId}, TermId: {TermId}")]
    private static partial void LogSchoolTermUpdated(ILogger logger, Guid schoolId, Guid termId);

    public sealed class Command : IRequest<GetSchoolTermById.Response>
    {
        /// <summary>From the route, which is authoritative; the body must not repeat it.</summary>
        public required Guid SchoolId { get; init; }

        /// <summary>From the route, which is authoritative; the body must not repeat it.</summary>
        public required Guid TermId { get; init; }

        public required string Name { get; init; }

        /// <summary>First day of the term, inclusive.</summary>
        public required DateOnly? StartDate { get; init; }

        /// <summary>Last day of the term, <b>inclusive</b>.</summary>
        public required DateOnly? EndDate { get; init; }

        /// <summary>
        ///     Required on a replace, and nullable so that "absent" is distinguishable from "false".
        /// </summary>
        /// <remarks>
        ///     A non-nullable <c>bool</c> would deserialise an omitted member to <c>false</c>, so every
        ///     partial update sent by a client that did not know about the flag would silently
        ///     deactivate the term — and silently free its dates for anything to be created over.
        /// </remarks>
        public required bool? IsActive { get; init; }
    }

    /// <summary>
    ///     The JSON payload. Separate from <see cref="Command" /> because the command carries the two
    ///     route values and conventions §2 forbids a body repeating them.
    /// </summary>
    public sealed class Body
    {
        public required string Name { get; init; }

        /// <summary>First day of the term, inclusive.</summary>
        public DateOnly? StartDate { get; init; }

        /// <summary>Last day of the term, <b>inclusive</b>.</summary>
        public DateOnly? EndDate { get; init; }

        /// <summary>Required — a <c>PUT</c> is a replace. Reactivation is <c>true</c> here.</summary>
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
                .MaximumLength(CreateSchoolTerm.MaxNameLength)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage(FormattableString.Invariant(
                    $"Name must not exceed {CreateSchoolTerm.MaxNameLength} characters."));

            RuleFor(command => command.StartDate)
                .NotNull()
                .WithErrorCode(ErrorCodes.Validation.RequiredField)
                .WithMessage("Start date is required.");

            RuleFor(command => command.EndDate)
                .Cascade(CascadeMode.Stop)
                .NotNull()
                .WithErrorCode(ErrorCodes.Validation.RequiredField)
                .WithMessage("End date is required.")
                .Must((command, endDate) =>
                    !command.StartDate.HasValue || endDate!.Value >= command.StartDate.Value)
                .WithErrorCode(ErrorCodes.Validation.Failed)
                .WithMessage("End date must not be before start date.");

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
        : IRequestHandler<Command, GetSchoolTermById.Response>
    {
        public async Task<GetSchoolTermById.Response> Handle(
            Command request,
            CancellationToken cancellationToken)
        {
            currentUser.EnsureAuthorized(request.SchoolId, ErrorCodes.School.NotFound);

            SchoolTerm term = await dbContext.SchoolTerms
                                 .FirstOrDefaultAsync(
                                     candidate => candidate.Id == request.TermId
                                                  && candidate.SchoolId == request.SchoolId,
                                     cancellationToken)
                             ?? throw new NotFoundException(ErrorCodes.Term.NotFound);

            term.Name = request.Name;
            term.StartDate = request.StartDate!.Value;
            term.EndDate = request.EndDate!.Value;

            // The whole activation half, and nothing else — no local
            // `if (term.IsActive != request.IsActive)`. The replace form is what a PUT means: a body
            // restating the current flag is not a transition. The validator guarantees IsActive is
            // present; a handler-tier caller that omits it is a programming error, not a request.
            ActivationPolicy.ApplyReplacement(
                term,
                request.IsActive!.Value,
                currentUser,
                ActivationPrivilege.SchoolScope,
                DeactivateSchoolTerm.ResourceName);

            // V-19, against the RESULTING state rather than the incoming flag. That is what makes one
            // branch cover both "the dates moved" and "the term was reactivated" — the second reads as
            // a flag flip and is the one flag flip that can violate the invariant, so attaching the
            // probe to a date-changed branch would leave reactivation unguarded.
            //
            // Skipped entirely when the result is inactive: an inactive term may overlap anything,
            // which is exactly what makes deactivation the way to supersede a term, and what makes
            // parking one of an overlapping pair the documented recovery.
            if (term.IsActive)
            {
                // AsNoTracking, so the probe reads the store rather than the tracked instance this
                // handler has already mutated. Excluding this term by id is what stops it conflicting
                // with its own former dates.
                SchoolTerm? conflict = await dbContext.SchoolTerms
                    .AsNoTracking()
                    .Where(TermOverlap.Overlapping(
                        request.SchoolId, term.StartDate, term.EndDate, term.Id))
                    .OrderBy(candidate => candidate.StartDate)
                    .ThenBy(candidate => candidate.Id)
                    .FirstOrDefaultAsync(cancellationToken);

                // Thrown before SaveChangesAsync, so the mutations above never reach the row.
                if (conflict is not null)
                {
                    throw new ConflictException(
                        ErrorCodes.Term.Overlap, CreateSchoolTerm.OverlapMessage(conflict));
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            LogSchoolTermUpdated(logger, term.SchoolId, term.Id);

            return GetSchoolTermById.ToResponse(term);
        }
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            app.MapPut("/schools/{schoolId}/terms/{termId}", async (
                    Guid schoolId,
                    Guid termId,
                    Body body,
                    IMediator mediator,
                    CancellationToken cancellationToken) =>
                {
                    GetSchoolTermById.Response response = await mediator.Send(
                        new Command
                        {
                            SchoolId = schoolId,
                            TermId = termId,
                            Name = body.Name,
                            StartDate = body.StartDate,
                            EndDate = body.EndDate,
                            IsActive = body.IsActive
                        },
                        cancellationToken);

                    return Results.Ok(response);
                })
                .WithName(nameof(UpdateSchoolTerm))
                .WithTags("SchoolTerms")
                .WithDescription(GetSchoolTermById.InclusiveBoundsDescription)
                .Produces<GetSchoolTermById.Response>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }
    }
}
