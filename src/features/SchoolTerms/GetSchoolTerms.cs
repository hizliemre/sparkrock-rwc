using Carter;
using domain.Exceptions;
using domain.SchoolTerms;
using domain.Security;
using features.Paging;
using FluentValidation;
using infra.persistence.sql;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace features.SchoolTerms;

/// <summary>
///     <c>GET /schools/{schoolId}/terms</c> — paged, chronological, active-only by default.
/// </summary>
/// <remarks>
///     Paged like every collection (conventions §2) even though a school has three or four terms a
///     year: the envelope is not optional and switching to one later is breaking.
/// </remarks>
public static partial class GetSchoolTerms
{
    public sealed class Query : IRequest<PagedResponse<GetSchoolTermById.Response>>
    {
        /// <summary>From the route, which is authoritative.</summary>
        public required Guid SchoolId { get; init; }

        public int? Page { get; init; }

        public int? PageSize { get; init; }

        /// <summary>
        ///     Defaults to false. Clears <b>O-08</b> for this aggregate.
        /// </summary>
        /// <remarks>
        ///     It matters more here than on the other three reference collections: deactivation is the
        ///     mechanism by which a superseded term is parked so a replacement can be created over its
        ///     dates, so a client with no way to list inactive terms cannot see why a <c>POST</c> was
        ///     rejected with <c>TERM.OVERLAP</c>, nor find the row to reactivate.
        /// </remarks>
        public bool IncludeInactive { get; init; }
    }

    internal sealed class QueryValidator : AbstractValidator<Query>
    {
        public QueryValidator()
        {
            RuleFor(query => query.Page).ValidPage();
            RuleFor(query => query.PageSize).ValidPageSize();
        }
    }

    internal sealed class QueryHandler(IDbContext dbContext, ICurrentUser currentUser)
        : IRequestHandler<Query, PagedResponse<GetSchoolTermById.Response>>
    {
        public async Task<PagedResponse<GetSchoolTermById.Response>> Handle(
            Query request,
            CancellationToken cancellationToken)
        {
            // Scope, then existence. Both answer 404 with an identical payload, so the order is not
            // observable from outside — but reversing it would issue a query on behalf of a caller
            // who may not address this school at all.
            currentUser.EnsureAuthorized(request.SchoolId, ErrorCodes.School.NotFound);

            // The school is an {id} in the path, and conventions §2 gives a path id that does not
            // resolve a 404. Returning an empty page would report "this school has no terms" about a
            // school that does not exist. One indexed primary-key lookup; the correctness is worth
            // the second round trip.
            if (!await dbContext.Schools.AnyAsync(school => school.Id == request.SchoolId, cancellationToken))
                throw new NotFoundException(ErrorCodes.School.NotFound);

            IQueryable<SchoolTerm> terms = dbContext.SchoolTerms
                .AsNoTracking()
                .Where(term => term.SchoolId == request.SchoolId);

            // By composition, never `term.IsActive || @includeInactive`: a disjunction against a
            // parameter defeats any index on the column.
            if (!request.IncludeInactive)
                terms = terms.Where(term => term.IsActive);

            // Total order (VC-27). StartDate alone is not unique — two terms can legitimately begin
            // on the same day once one of them is inactive — and a non-total order under the global
            // SplitQuery setting can repeat a row on one page and drop another.
            return await terms
                .OrderBy(term => term.StartDate)
                .ThenBy(term => term.Id)
                .Select(GetSchoolTermById.Projection)
                .ToPagedResponseAsync(request.Page, request.PageSize, cancellationToken);
        }
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            app.MapGet("/schools/{schoolId}/terms", async (
                    Guid schoolId,
                    int? page,
                    int? pageSize,
                    bool? includeInactive,
                    IMediator mediator,
                    CancellationToken cancellationToken) =>
                {
                    PagedResponse<GetSchoolTermById.Response> terms = await mediator.Send(
                        new Query
                        {
                            SchoolId = schoolId,
                            Page = page,
                            PageSize = pageSize,
                            IncludeInactive = includeInactive ?? false
                        },
                        cancellationToken);

                    return Results.Ok(terms);
                })
                .WithName(nameof(GetSchoolTerms))
                .WithTags("SchoolTerms")
                .WithDescription(GetSchoolTermById.InclusiveBoundsDescription)
                .Produces<PagedResponse<GetSchoolTermById.Response>>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }
    }
}
