using System.Reflection;
using domain.AttendanceCodes;
using domain.Exceptions;
using features.AttendanceCodes;
using features.tests.Fakes;
using FluentValidation.Results;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace features.tests.AttendanceCodes;

public sealed class CreateAttendanceCodeValidatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WhenValueIsEmpty_Fails(string? value)
    {
        ValidationFailure failure = Assert.Single(Validate(value: value!).Errors);

        Assert.Equal(nameof(CreateAttendanceCode.Command.Value), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.RequiredField, failure.ErrorCode);
    }

    /// <summary>
    ///     Matches <c>varchar(5)</c> from F01c §3 and legacy's <c>AttendCode VARCHAR(5)</c>. A longer
    ///     value would otherwise be a 500 at insert rather than a field error.
    /// </summary>
    [Fact]
    public void Validate_WhenValueExceedsFiveCharacters_Fails()
    {
        ValidationFailure failure = Assert.Single(Validate(value: "ABCDEF").Errors);

        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
    }

    /// <summary>The length rule is applied to the trimmed value, because that is what gets stored.</summary>
    [Fact]
    public void Validate_WhenValueIsFiveCharactersAfterTrimming_Succeeds() =>
        Assert.True(Validate(value: "  ABCDE  ").IsValid);

    [Fact]
    public void Validate_WhenValueContainsWhitespace_Fails()
    {
        ValidationFailure failure = Assert.Single(Validate(value: "A B").Errors);

        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenDescriptionIsEmpty_Fails()
    {
        ValidationFailure failure = Assert.Single(Validate(description: "").Errors);

        Assert.Equal(nameof(CreateAttendanceCode.Command.Description), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.RequiredField, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenDescriptionExceeds100_Fails()
    {
        ValidationFailure failure = Assert.Single(Validate(description: new string('x', 101)).Errors);

        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
    }

    [Theory]
    [InlineData("IsAbsent")]
    [InlineData("IsExcused")]
    public void Validate_WhenAFlagIsAbsent_Fails(string property)
    {
        ValidationFailure failure = Assert.Single(
            Validate(
                isAbsent: property == "IsAbsent" ? null : true,
                isExcused: property == "IsExcused" ? null : false).Errors);

        Assert.Equal(property, failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.RequiredField, failure.ErrorCode);
    }

    /// <summary>
    ///     Deliberately permitted. F01c ships no such constraint and F12 must be able to import
    ///     whatever legacy holds; a rule the data can violate is a rule that produces unimportable
    ///     history.
    /// </summary>
    [Fact]
    public void Validate_WhenExcusedWithoutAbsent_Succeeds() =>
        Assert.True(Validate(isAbsent: false, isExcused: true).IsValid);

    [Fact]
    public void Validate_WhenLowerCase_Succeeds() => Assert.True(Validate(value: "a").IsValid);

    private static ValidationResult Validate(
        string value = AttendanceCodeSeed.DefaultValue,
        string description = AttendanceCodeSeed.DefaultDescription,
        bool? isAbsent = true,
        bool? isExcused = false)
    {
        CreateAttendanceCode.CommandValidator validator = new();

        return validator.Validate(new CreateAttendanceCode.Command
        {
            Value = value,
            Description = description,
            IsAbsent = isAbsent,
            IsExcused = isExcused
        });
    }
}

public sealed class CreateAttendanceCodeHandlerTests
{
    /// <summary>
    ///     <b>V-27's <c>Verified by</c>, half one.</b> The other half is the integration-tier
    ///     assertion that <c>"a"</c> then <c>"A"</c> collides on the unfiltered unique index.
    /// </summary>
    [Fact]
    public async Task Handle_NormalisesValueToUpperCase()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        GetAttendanceCodeById.Response response = await Handle(dbContext, value: "a");

        AttendanceCode stored = await dbContext.AttendanceCodes.SingleAsync();

        Assert.Equal("A", stored.Value);
        Assert.Equal("A", response.Value);
    }

    [Fact]
    public async Task Handle_TrimsTheValueBeforeStoringIt()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        await Handle(dbContext, value: "  ex  ");

        Assert.Equal("EX", (await dbContext.AttendanceCodes.SingleAsync()).Value);
    }

    [Fact]
    public async Task Handle_ReturnsTheCreatedResponseWithTheNormalisedValue()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        GetAttendanceCodeById.Response response = await Handle(
            dbContext,
            value: "l",
            description: "Late",
            isAbsent: false,
            isExcused: false);

        Assert.NotEqual(Guid.Empty, response.Id);
        Assert.Equal("L", response.Value);
        Assert.Equal("Late", response.Description);
        Assert.False(response.IsAbsent);
        Assert.False(response.IsExcused);
        Assert.True(response.IsActive);
        Assert.Equal(InMemoryDbContextFactory.DefaultNow, response.CreatedAt);
        Assert.Equal(InMemoryDbContextFactory.DefaultNow, response.LastUpdatedAt);
    }

    [Fact]
    public async Task Handle_PersistsTheCodeAsActive()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        await Handle(dbContext);

        Assert.True((await dbContext.AttendanceCodes.SingleAsync()).IsActive);
    }

    /// <summary>
    ///     An inference beyond DEC-20, recorded as one. <c>Value</c> is unique <em>unfiltered</em>, so
    ///     a created code occupies its value permanently — deactivation never frees it and there is no
    ///     delete. A non-admin who can <c>POST</c> can permanently consume any string in a
    ///     five-character namespace every school shares.
    /// </summary>
    [Fact]
    public async Task Handle_WhenCallerIsNotSystemAdmin_ThrowsForbidden()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        ForbiddenException exception = await Assert.ThrowsAsync<ForbiddenException>(
            () => Handle(dbContext, currentUser: FakeCurrentUser.ScopedTo(Guid.NewGuid())));

        Assert.Equal(ErrorCodes.System.Forbidden, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WhenCallerIsNotSystemAdmin_WritesNothing()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        await Assert.ThrowsAsync<ForbiddenException>(() => Handle(dbContext, currentUser: new FakeCurrentUser()));

        Assert.Empty(await dbContext.AttendanceCodes.ToListAsync());
    }

    /// <summary>
    ///     New codes are active. Accepting the flag here would be a third path to an inactive code and
    ///     the only one that is not a transition, so it would bypass the DEC-20 privilege check that
    ///     <c>ActivationPolicy</c> exists to centralise.
    /// </summary>
    [Fact]
    public void Handle_DoesNotAcceptAnActiveFlag() =>
        Assert.DoesNotContain(
            typeof(CreateAttendanceCode.Command).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            property => property.Name == "IsActive");

    /// <summary>
    ///     <b>No pre-<c>SELECT</c> for duplicates.</b> The unfiltered unique index is the only race-free
    ///     authority and the 409 is the integration tier's to assert — EF InMemory enforces no unique
    ///     index, so a handler-tier 409 test would pass only because the test itself threw. What is
    ///     assertable here is that no read of <c>AttendanceCodes</c> precedes the insert, which is the
    ///     thing a well-meaning reviewer adds back.
    /// </summary>
    [Fact]
    public async Task Handle_WhenTheValueIsAlreadyTaken_DoesNotPreCheckAndSimplyInserts()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await AttendanceCodeSeed.AddAsync(dbContext, value: "A");

        GetAttendanceCodeById.Response response = await Handle(dbContext, value: "a");

        Assert.Equal("A", response.Value);
        Assert.Equal(2, await dbContext.AttendanceCodes.CountAsync());
    }

    internal static Task<GetAttendanceCodeById.Response> Handle(
        SparkrockRwcDbContext dbContext,
        string value = AttendanceCodeSeed.DefaultValue,
        string description = AttendanceCodeSeed.DefaultDescription,
        bool isAbsent = true,
        bool isExcused = false,
        FakeCurrentUser? currentUser = null)
    {
        CreateAttendanceCode.CommandHandler handler = new(
            dbContext,
            currentUser ?? FakeCurrentUser.SystemAdmin(),
            NullLogger<CreateAttendanceCode.CommandHandler>.Instance);

        return handler.Handle(
            new CreateAttendanceCode.Command
            {
                Value = value,
                Description = description,
                IsAbsent = isAbsent,
                IsExcused = isExcused
            },
            CancellationToken.None);
    }
}
