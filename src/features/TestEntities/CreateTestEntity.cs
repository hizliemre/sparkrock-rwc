using Carter;
using domain;
using domain.Exceptions;
using FluentValidation;
using infra.persistence.sql;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace features.TestEntities;

public static partial class CreateTestEntity
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "TestEntity created with Id: {Id} and Name: {Name}")]
    private static partial void LogTestEntityCreated(ILogger logger, Guid id, string name);

    public sealed class Command : IRequest
    {
        public required string TestProperty { get; init; }
    }
    
    internal sealed class CommandValidator: AbstractValidator<Command>
    {
        public CommandValidator()
        {
            RuleFor(x => x.TestProperty).NotEmpty()
                .WithErrorCode(ErrorCodes.VALIDATION_REQUIRED_FIELD)
                .WithMessage("TestProperty is required");
        }
    }
    
    internal sealed class CommandHandler(IDbContext dbContext, ILogger<CommandHandler> logger):IRequestHandler<Command>
    {
        public async Task Handle(Command request, CancellationToken cancellationToken)
        {
            TestEntity testEntity = new()
            {
                TestProperty = request.TestProperty
            };

            dbContext.TestEntities.Add(testEntity);
            await dbContext.SaveChangesAsync(cancellationToken);

            LogTestEntityCreated(logger, testEntity.Id, testEntity.TestProperty);
        }
    }
    
    public sealed class Endpoint: ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            app.MapPost("/test-entities", async (Command command, IMediator mediator) =>
            {
                await mediator.Send(command);
                return Results.Ok();
            })
            .WithName("CreateTestEntity")
            .WithTags("TestEntities");
        }
    }
}