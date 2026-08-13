using System.Linq.Expressions;
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

namespace features.SchoolTerms;

/// <summary>
///     <c>GET /schools/{schoolId}/terms/{termId}</c>, and the home of the <see cref="Response" /> every
///     SchoolTerms slice returns.
/// </summary>
/// <remarks>
///     The response shape is declared once here rather than per slice, so a field added to a term is
///     added in one place and appears on the create, update and list payloads at the same time.
/// </remarks>
public static partial class GetSchoolTermById
{
    /// <summary>
    ///     The statement of the one date convention this aggregate does not share with the rest of the
    ///     system, carried on every route that reads or writes a term's dates.
    /// </summary>
    /// <remarks>
    ///     Every other range in the API is half-open <c>[from, toExclusive)</c>. A client that reads
    ///     <c>endDate</c> as exclusive loses the last day of every term, and there is nothing in the
    ///     payload that would show it.
    /// </remarks>
    public const string InclusiveBoundsDescription =
        "startDate and endDate are both INCLUSIVE: a term of [2026-09-01, 2026-12-20] contains "
        + "2026-12-20. This is the one closed range in the API — every other date range is half-open "
        + "[from, toExclusive). Two active terms of one school may not share a day, so a term starting "
        + "on 2026-12-20 conflicts and one starting on 2026-12-21 does not.";

    /// <summary>
    ///     The projection every SchoolTerms slice reads through, so the five routes cannot drift.
    /// </summary>
    /// <remarks>
    ///     An <see cref="Expression{TDelegate}" /> rather than a method, because it has to translate to
    ///     SQL for the list and single-item reads. <see cref="ToResponse" /> compiles it once for the
    ///     write slices, which already hold the tracked entity and have nothing to translate.
    /// </remarks>
    internal static readonly Expression<Func<SchoolTerm, Response>> Projection = term => new Response
    {
        Id = term.Id,
        SchoolId = term.SchoolId,
        Name = term.Name,
        StartDate = term.StartDate,
        EndDate = term.EndDate,
        IsActive = term.IsActive,
        CreatedAt = term.CreatedAt,

        // V-21's global rule. The interceptor leaves ModifiedAt null until a row is actually
        // modified, so an unmodified row would otherwise report no timestamp at all.
        LastUpdatedAt = term.ModifiedAt ?? term.CreatedAt
    };

    private static readonly Func<SchoolTerm, Response> CompiledProjection = Projection.Compile();

    public sealed class Query : IRequest<Response>
    {
        public required Guid SchoolId { get; init; }

        public required Guid TermId { get; init; }
    }

    public sealed record Response
    {
        public required Guid Id { get; init; }

        public required Guid SchoolId { get; init; }

        public required string Name { get; init; }

        /// <summary>First day of the term, <b>inclusive</b>.</summary>
        public required DateOnly StartDate { get; init; }

        /// <summary>
        ///     Last day of the term, <b>inclusive</b> — see
        ///     <see cref="InclusiveBoundsDescription" />.
        /// </summary>
        public required DateOnly EndDate { get; init; }

        public required bool IsActive { get; init; }

        public required DateTimeOffset CreatedAt { get; init; }

        public required DateTimeOffset LastUpdatedAt { get; init; }
    }

    /// <summary>Projects an already-materialised term, for the write slices.</summary>
    internal static Response ToResponse(SchoolTerm term) => CompiledProjection(term);

    internal sealed class QueryHandler(IDbContext dbContext, ICurrentUser currentUser)
        : IRequestHandler<Query, Response>
    {
        public async Task<Response> Handle(Query request, CancellationToken cancellationToken)
        {
            // Scope first, then existence. Every route in this feature is path-school, so the school
            // is resolved before the term and a school outside AuthorizedSchoolIds is a 404 whose
            // payload is identical to an absent one — NotFoundException carries no message.
            currentUser.EnsureAuthorized(request.SchoolId, ErrorCodes.School.NotFound);

            // Keyed on BOTH ids. Keying on the term alone would serve another school's term through
            // this school's path to any caller authorised for this school — the tenancy hole the
            // nested route exists to close. The school-level check has already passed, so a
            // term-specific code discloses nothing beyond "not here".
            Response? response = await dbContext.SchoolTerms
                .AsNoTracking()
                .Where(term => term.Id == request.TermId && term.SchoolId == request.SchoolId)
                .Select(Projection)
                .FirstOrDefaultAsync(cancellationToken);

            // Inactive terms return 200 with isActive: false. DEC-19 makes deactivation hide a
            // resource from default list results and nothing more, and F08 renders history against
            // superseded terms.
            return response ?? throw new NotFoundException(ErrorCodes.Term.NotFound);
        }
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            app.MapGet("/schools/{schoolId}/terms/{termId}", async (
                    Guid schoolId,
                    Guid termId,
                    IMediator mediator,
                    CancellationToken cancellationToken) =>
                {
                    Response response = await mediator.Send(
                        new Query { SchoolId = schoolId, TermId = termId }, cancellationToken);

                    return Results.Ok(response);
                })
                .WithName(nameof(GetSchoolTermById))
                .WithTags("SchoolTerms")
                .WithDescription(InclusiveBoundsDescription)
                .Produces<Response>(StatusCodes.Status200OK)

                // Spec §7: 404 only. No 400 — both route values bind as Guid, and a malformed one is
                // the kernel's SYSTEM.MALFORMED_REQUEST rather than this route's contract. No 403
                // anywhere in this feature.
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
    }
}
