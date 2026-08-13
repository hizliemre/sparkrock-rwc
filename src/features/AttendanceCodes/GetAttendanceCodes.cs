using Carter;
using domain.AttendanceCodes;
using features.Paging;
using FluentValidation;
using infra.persistence.sql;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace features.AttendanceCodes;

/// <summary>
///     <c>GET /attendance-codes</c> — paged, active-only by default, global to every caller.
/// </summary>
/// <remarks>
///     <b>No <c>?q</c>.</b> The whole table is a handful of rows and every client can hold it in
///     memory; a search filter over five rows is API surface with no consumer. There is no sort
///     parameter either — conventions §2 bans client-supplied sorts outright.
/// </remarks>
public static partial class GetAttendanceCodes
{
    public sealed class Query : IRequest<PagedResponse<GetAttendanceCodeById.Response>>
    {
        public int? Page { get; init; }

        public int? PageSize { get; init; }

        /// <summary>
        ///     Defaults to false. Closes O-08 for this aggregate: the route table gave
        ///     <c>?includeInactive</c> to F02 and F05 only, and DEC-19 requires F08 to render history
        ///     whose code has since been deactivated — so a client needs somewhere to fetch the
        ///     descriptions of codes the default list hides.
        /// </summary>
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

    /// <summary>
    ///     Takes <see cref="IDbContext" /> and nothing else — the aggregate is unscoped by design
    ///     (O-03). See <see cref="GetAttendanceCodeById.QueryHandler" />.
    /// </summary>
    internal sealed class QueryHandler(IDbContext dbContext)
        : IRequestHandler<Query, PagedResponse<GetAttendanceCodeById.Response>>
    {
        public async Task<PagedResponse<GetAttendanceCodeById.Response>> Handle(
            Query request,
            CancellationToken cancellationToken)
        {
            IQueryable<AttendanceCode> codes = dbContext.AttendanceCodes.AsNoTracking();

            // By composition, never `code.IsActive || @includeInactive`: a disjunction against a
            // parameter defeats any index on the column.
            if (!request.IncludeInactive)
                codes = codes.Where(code => code.IsActive);

            // Total order (VC-27). Value is unique in the database, but the tie-break is not
            // decoration: a non-total order under the global SplitQuery setting can repeat a row on
            // one page and drop another.
            return await codes
                .OrderBy(code => code.Value)
                .ThenBy(code => code.Id)
                .Select(GetAttendanceCodeById.Projection)
                .ToPagedResponseAsync(request.Page, request.PageSize, cancellationToken);
        }
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            app.MapGet("/attendance-codes", async (
                    int? page,
                    int? pageSize,
                    bool? includeInactive,
                    IMediator mediator,
                    CancellationToken cancellationToken) =>
                {
                    PagedResponse<GetAttendanceCodeById.Response> codes = await mediator.Send(
                        new Query
                        {
                            Page = page,
                            PageSize = pageSize,
                            IncludeInactive = includeInactive ?? false
                        },
                        cancellationToken);

                    return Results.Ok(codes);
                })
                .WithName(nameof(GetAttendanceCodes))
                .WithTags("AttendanceCodes")
                .Produces<PagedResponse<GetAttendanceCodeById.Response>>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status400BadRequest);
        }
    }
}
