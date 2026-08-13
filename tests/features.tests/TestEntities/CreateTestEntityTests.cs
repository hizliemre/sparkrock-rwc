using domain;
using domain.Exceptions;
using features.TestEntities;
using FluentValidation.Results;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace features.tests.TestEntities;

public sealed class CreateTestEntityValidatorTests
{
    private readonly CreateTestEntity.CommandValidator _validator = new();

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    public void Validate_WhenTestPropertyIsBlank_Fails(string testProperty)
    {
        CreateTestEntity.Command command = new() { TestProperty = testProperty };

        ValidationResult result = _validator.Validate(command);

        Assert.False(result.IsValid);
        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(CreateTestEntity.Command.TestProperty), failure.PropertyName);
        Assert.Equal(ErrorCodes.VALIDATION_REQUIRED_FIELD, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenTestPropertyIsNull_Fails()
    {
        CreateTestEntity.Command command = new() { TestProperty = null! };

        ValidationResult result = _validator.Validate(command);

        Assert.False(result.IsValid);
        Assert.Equal(ErrorCodes.VALIDATION_REQUIRED_FIELD, Assert.Single(result.Errors).ErrorCode);
    }

    [Fact]
    public void Validate_WhenTestPropertyHasValue_Succeeds()
    {
        CreateTestEntity.Command command = new() { TestProperty = "a value" };

        ValidationResult result = _validator.Validate(command);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }
}

public sealed class CreateTestEntityHandlerTests
{
    [Fact]
    public async Task Handle_PersistsEntityWithGivenProperty()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        CreateTestEntity.CommandHandler handler = new(dbContext, NullLogger<CreateTestEntity.CommandHandler>.Instance);

        await handler.Handle(new CreateTestEntity.Command { TestProperty = "persisted value" }, CancellationToken.None);

        TestEntity persisted = Assert.Single(await dbContext.TestEntities.ToListAsync());
        Assert.Equal("persisted value", persisted.TestProperty);
    }

    [Fact]
    public async Task Handle_WhenCalledRepeatedly_AddsOneRowPerCall()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        CreateTestEntity.CommandHandler handler = new(dbContext, NullLogger<CreateTestEntity.CommandHandler>.Instance);

        await handler.Handle(new CreateTestEntity.Command { TestProperty = "first" }, CancellationToken.None);
        await handler.Handle(new CreateTestEntity.Command { TestProperty = "second" }, CancellationToken.None);

        List<string> persisted = await dbContext.TestEntities
            .Select(testEntity => testEntity.TestProperty)
            .ToListAsync();

        Assert.Equal(2, persisted.Count);
        Assert.Contains("first", persisted);
        Assert.Contains("second", persisted);
    }

    [Fact]
    public async Task Handle_DoesNotLeaveUnsavedChanges()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        CreateTestEntity.CommandHandler handler = new(dbContext, NullLogger<CreateTestEntity.CommandHandler>.Instance);

        await handler.Handle(new CreateTestEntity.Command { TestProperty = "saved" }, CancellationToken.None);

        Assert.False(dbContext.ChangeTracker.HasChanges());
    }
}
