using System.Globalization;
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
///     <c>POST /schools/{schoolId}/terms</c> — 201 with a <c>Location</c> header and the created term.
/// </summary>
/// <remarks>
///     This slice and <see cref="UpdateSchoolTerm" /> are the two enforcement points for <b>V-19</b>:
///     two active terms of one school may not share a day. Legacy resolved a date to a term with
///     <c>BETWEEN</c> and no ordering, so under overlap an arbitrary term won, differently per query
///     plan (D-03). The fix is not a deterministic read; it is making the overlapping state
///     unreachable.
///     <para>
///         <b>The invariant is not enforced by the database.</b> Two concurrent <c>POST</c>s can both
///         pass the probe and both commit, and nothing detects the pair afterwards. F01c's plan
///         accepted this with reasoning: an exclusion constraint needs <c>btree_gist</c>, raw
///         <c>migrationBuilder.Sql</c>, a <c>23P01</c> row in conventions §5 and a fourth exception
///         shape in the <c>SaveChangesAsync</c> override — four mechanisms for a table with a handful
///         of rows per school per year, created a few times a year by an administrator. Recovery for
///         an observed pair is <c>PUT { "isActive": false }</c> on one of them.
///     </para>
/// </remarks>
public static partial class CreateSchoolTerm
{
    /// <summary>Mirrors <c>school_terms.name varchar(100)</c>.</summary>
    public const int MaxNameLength = 100;

    [LoggerMessage(
        EventId = 1400,
        Level = LogLevel.Information,
        Message = "School term created. SchoolId: {SchoolId}, TermId: {TermId}")]
    private static partial void LogSchoolTermCreated(ILogger logger, Guid schoolId, Guid termId);

    /// <summary>
    ///     No <c>IsActive</c> member, deliberately.
    /// </summary>
    /// <remarks>
    ///     New terms are active. Accepting the flag would be a path to an inactive term that is not a
    ///     transition, so it would bypass the <c>ActivationPolicy</c> the other two write slices route
    ///     through — and an inactive term skips the overlap probe, so the row would sit waiting to
    ///     violate the invariant on its first reactivation.
    /// </remarks>
    public sealed class Command : IRequest<GetSchoolTermById.Response>
    {
        /// <summary>From the route, which is authoritative; the body must not repeat it.</summary>
        public required Guid SchoolId { get; init; }

        public required string Name { get; init; }

        /// <summary>
        ///     First day of the term, inclusive. Nullable so "absent" is distinguishable from a
        ///     default <see cref="DateOnly" />; a non-nullable member would bind an omitted field to
        ///     0001-01-01 and store it. The validator is what rejects the absence, so the answer is a
        ///     <c>VALIDATION.REQUIRED_FIELD</c> 400 rather than a binder-level malformed-request one.
        /// </summary>
        public required DateOnly? StartDate { get; init; }

        /// <summary>Last day of the term, <b>inclusive</b>. See <see cref="StartDate" />.</summary>
        public required DateOnly? EndDate { get; init; }
    }

    /// <summary>
    ///     The JSON payload. Separate from <see cref="Command" /> because the command carries the
    ///     route value and conventions §2 forbids a body repeating <c>schoolId</c>.
    /// </summary>
    public sealed class Body
    {
        public required string Name { get; init; }

        /// <summary>First day of the term, inclusive.</summary>
        public DateOnly? StartDate { get; init; }

        /// <summary>Last day of the term, <b>inclusive</b>.</summary>
        public DateOnly? EndDate { get; init; }
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
        }
    }

    /// <summary>
    ///     The <c>TERM.OVERLAP</c> detail, shared by the create and update slices.
    /// </summary>
    /// <remarks>
    ///     Carries the conflicting term's name and its two dates and nothing else. Those are bounded
    ///     structured values, which conventions §2 permits a message to echo; a free-text column is
    ///     never echoed. Without them the client is told only that <em>something</em> collided, and a
    ///     school's terms are exactly the set it would then have to list to find out.
    ///     <para>
    ///         Dates are rendered ISO 8601 and invariant. <c>DateOnly.ToString()</c> is
    ///         culture-sensitive, so a server running under a different culture would emit
    ///         <c>20/12/2026</c> into a contract that is dated <c>2026-12-20</c> everywhere else.
    ///     </para>
    /// </remarks>
    internal static string OverlapMessage(SchoolTerm conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);

        string startDate = conflict.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string endDate = conflict.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return "The dates overlap the term '" + conflict.Name + "' (" + startDate + " to " + endDate
               + ", both inclusive). Deactivate that term first to supersede it.";
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

            if (!await dbContext.Schools.AnyAsync(school => school.Id == request.SchoolId, cancellationToken))
                throw new NotFoundException(ErrorCodes.School.NotFound);

            // V-19. Terms are created active, so the probe always applies; Guid.Empty excludes
            // nothing, because there is no row yet to exclude. Ordered so the term named in the
            // message is the earliest conflicting one rather than whichever the plan happened to
            // return — the same nondeterminism D-03 records in the legacy lookup.
            SchoolTerm? conflict = await dbContext.SchoolTerms
                .AsNoTracking()
                .Where(TermOverlap.Overlapping(
                    request.SchoolId, request.StartDate!.Value, request.EndDate!.Value, Guid.Empty))
                .OrderBy(candidate => candidate.StartDate)
                .ThenBy(candidate => candidate.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (conflict is not null)
                throw new ConflictException(ErrorCodes.Term.Overlap, OverlapMessage(conflict));

            SchoolTerm term = new()
            {
                SchoolId = request.SchoolId,
                Name = request.Name,
                StartDate = request.StartDate!.Value,
                EndDate = request.EndDate!.Value
            };

            dbContext.SchoolTerms.Add(term);
            await dbContext.SaveChangesAsync(cancellationToken);

            LogSchoolTermCreated(logger, term.SchoolId, term.Id);

            return GetSchoolTermById.ToResponse(term);
        }
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            app.MapPost("/schools/{schoolId}/terms", async (
                    Guid schoolId,
                    Body body,
                    IMediator mediator,
                    CancellationToken cancellationToken) =>
                {
                    GetSchoolTermById.Response response = await mediator.Send(
                        new Command
                        {
                            SchoolId = schoolId,
                            Name = body.Name,
                            StartDate = body.StartDate,
                            EndDate = body.EndDate
                        },
                        cancellationToken);

                    return Results.Created($"/api/v1/schools/{schoolId}/terms/{response.Id}", response);
                })
                .WithName(nameof(CreateSchoolTerm))
                .WithTags("SchoolTerms")
                .WithDescription(GetSchoolTermById.InclusiveBoundsDescription)
                .Produces<GetSchoolTermById.Response>(StatusCodes.Status201Created)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);
        }
    }
}
