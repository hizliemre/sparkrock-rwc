using Carter;
using features.Paging;
using FluentValidation;
using infra.persistence.sql;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace features.TestEntities;

public static class GetTestEntities
{
    public sealed class Query : IRequest<PagedResponse<Response>>
    {
        public int? Page { get; init; }

        public int? PageSize { get; init; }
    }

    public sealed record Response
    {
        public required Guid Id { get; init; }
        public required string TestProperty { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
    }

    internal sealed class QueryValidator : AbstractValidator<Query>
    {
        public QueryValidator()
        {
            RuleFor(query => query.Page).ValidPage();
            RuleFor(query => query.PageSize).ValidPageSize();
        }
    }

    internal sealed class QueryHandler(IDbContext dbContext) : IRequestHandler<Query, PagedResponse<Response>>
    {
        public async Task<PagedResponse<Response>> Handle(Query request, CancellationToken cancellationToken)
        {
            // The documented default sort for this resource, and it is total: CreatedAt alone is not
            // unique, and a non-unique order under the global SplitQuery setting can repeat a row on one
            // page and drop another (VC-27). Every default sort ends in Id for that reason.
            return await dbContext.TestEntities
                .AsNoTracking()
                .OrderByDescending(testEntity => testEntity.CreatedAt)
                .ThenBy(testEntity => testEntity.Id)
                .Select(testEntity => new Response
                {
                    Id = testEntity.Id,
                    TestProperty = testEntity.TestProperty,
                    CreatedAt = testEntity.CreatedAt
                })
                .ToPagedResponseAsync(request.Page, request.PageSize, cancellationToken);
        }
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            app.MapGet("/test-entities", async (int? page, int? pageSize, IMediator mediator, CancellationToken cancellationToken) =>
            {
                PagedResponse<Response> testEntities = await mediator.Send(
                    new Query { Page = page, PageSize = pageSize },
                    cancellationToken);

                return Results.Ok(testEntities);
            })
            .WithName("GetTestEntities")
            .WithTags("TestEntities");
        }
    }
}
