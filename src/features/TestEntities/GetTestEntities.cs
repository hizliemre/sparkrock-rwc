using Carter;
using infra.persistence.sql;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace features.TestEntities;

public static class GetTestEntities
{
    public sealed class Query : IRequest<IReadOnlyList<Response>>
    {
    }

    public sealed record Response
    {
        public required Guid Id { get; init; }
        public required string TestProperty { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
    }

    internal sealed class QueryHandler(IDbContext dbContext) : IRequestHandler<Query, IReadOnlyList<Response>>
    {
        public async Task<IReadOnlyList<Response>> Handle(Query request, CancellationToken cancellationToken)
        {
            List<Response> testEntities = await dbContext.TestEntities
                .AsNoTracking()
                .OrderByDescending(testEntity => testEntity.CreatedAt)
                .Select(testEntity => new Response
                {
                    Id = testEntity.Id,
                    TestProperty = testEntity.TestProperty,
                    CreatedAt = testEntity.CreatedAt
                })
                .ToListAsync(cancellationToken);

            return testEntities;
        }
    }

    public sealed class Endpoint : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            app.MapGet("/test-entities", async (IMediator mediator, CancellationToken cancellationToken) =>
            {
                IReadOnlyList<Response> testEntities = await mediator.Send(new Query(), cancellationToken);
                return Results.Ok(testEntities);
            })
            .WithName("GetTestEntities")
            .WithTags("TestEntities");
        }
    }
}
