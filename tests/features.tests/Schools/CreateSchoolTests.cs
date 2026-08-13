using System.Reflection;
using domain.Attendance;
using domain.Exceptions;
using domain.Schools;
using features.Schools;
using features.tests.Fakes;
using FluentValidation.Results;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace features.tests.Schools;

public sealed class CreateSchoolValidatorTests
{
    private readonly CreateSchool.CommandValidator _validator = new();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WhenNameIsEmpty_Fails(string name)
    {
        ValidationResult result = _validator.Validate(Command(name: name));

        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(CreateSchool.Command.Name), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.RequiredField, failure.ErrorCode);
    }

    /// <summary>
    ///     200 matches the <c>varchar(200)</c> column exactly. A looser validator lets Postgres reject
    ///     what the validator allowed, which surfaces as a 500 rather than a 400.
    /// </summary>
    [Fact]
    public void Validate_WhenNameExceeds200_Fails()
    {
        ValidationResult result = _validator.Validate(Command(name: new string('a', 201)));

        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(CreateSchool.Command.Name), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenNameIsExactly200_Succeeds()
    {
        Assert.True(_validator.Validate(Command(name: new string('a', 200))).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WhenTimeZoneIdIsEmpty_Fails(string timeZoneId)
    {
        ValidationResult result = _validator.Validate(Command(timeZoneId: timeZoneId));

        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(CreateSchool.Command.TimeZoneId), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.RequiredField, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenTimeZoneIdExceeds64_Fails()
    {
        ValidationResult result = _validator.Validate(Command(timeZoneId: new string('a', 65)));

        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(CreateSchool.Command.TimeZoneId), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
    }

    /// <summary>
    ///     F01c deferred this rule explicitly and named F02 as its owner. Without it an unresolvable
    ///     zone reaches the save path and throws <c>TimeZoneNotFoundException</c> at write time
    ///     (DEC-12) — a 500 for what is a field error.
    /// </summary>
    [Fact]
    public void Validate_WhenTimeZoneIdIsNotAKnownZone_Fails()
    {
        ValidationResult result = _validator.Validate(Command(timeZoneId: "Not/AZone"));

        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(CreateSchool.Command.TimeZoneId), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
    }

    /// <summary>
    ///     On .NET 8, <c>FindSystemTimeZoneById</c> accepts IANA ids on every platform through ICU, so
    ///     this resolves on Windows too. The rule catches typos, not platform differences.
    /// </summary>
    [Fact]
    public void Validate_WhenTimeZoneIdIsIana_Succeeds()
    {
        Assert.True(_validator.Validate(Command(timeZoneId: "America/Toronto")).IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WhenThresholdIsZeroOrNegative_Fails(int threshold)
    {
        ValidationResult result = _validator.Validate(Command(threshold: threshold));

        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(CreateSchool.Command.AbsenceAlertThreshold), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
    }

    /// <summary>Null means "use the domain default" (V-26), not "invalid".</summary>
    [Fact]
    public void Validate_WhenThresholdIsAbsent_Succeeds()
    {
        Assert.True(_validator.Validate(Command(threshold: null)).IsValid);
    }

    private static CreateSchool.Command Command(
        string name = SchoolSeed.DefaultName,
        string timeZoneId = SchoolSeed.DefaultTimeZoneId,
        int? threshold = 12) =>
        new() { Name = name, TimeZoneId = timeZoneId, AbsenceAlertThreshold = threshold };
}

public sealed class CreateSchoolHandlerTests
{
    [Fact]
    public async Task Handle_PersistsTheSchoolAsActive()
    {
        FakeCurrentUser admin = FakeCurrentUser.SystemAdmin();
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: admin);

        await Handle(dbContext, admin, new CreateSchool.Command
        {
            Name = "Rideau Demo School",
            TimeZoneId = "America/Toronto",
            AbsenceAlertThreshold = 12
        });

        School persisted = Assert.Single(await dbContext.Schools.ToListAsync());
        Assert.Equal("Rideau Demo School", persisted.Name);
        Assert.Equal("America/Toronto", persisted.TimeZoneId);
        Assert.Equal(12, persisted.AbsenceAlertThreshold);
        Assert.True(persisted.IsActive);
    }

    [Fact]
    public async Task Handle_ReturnsTheCreatedResponse()
    {
        FakeCurrentUser admin = FakeCurrentUser.SystemAdmin();
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: admin);

        GetSchoolById.Response response = await Handle(dbContext, admin, new CreateSchool.Command
        {
            Name = "Rideau Demo School",
            TimeZoneId = "America/Toronto",
            AbsenceAlertThreshold = null
        });

        School persisted = Assert.Single(await dbContext.Schools.ToListAsync());
        Assert.Equal(persisted.Id, response.Id);
        Assert.NotEqual(Guid.Empty, response.Id);
        Assert.Equal("Rideau Demo School", response.Name);
        Assert.True(response.IsActive);
        Assert.Null(response.AbsenceAlertThreshold);
        Assert.Equal(AbsenceRules.DefaultThreshold, response.EffectiveAbsenceAlertThreshold);
        Assert.Equal(InMemoryDbContextFactory.DefaultNow, response.CreatedAt);
        Assert.Equal(response.CreatedAt, response.LastUpdatedAt);
    }

    /// <summary>
    ///     An inference beyond DEC-20, recorded as one in the spec: a non-admin's scope is a fixed list
    ///     of school ids, so a school they create is one they immediately cannot see — a write with no
    ///     readable effect, and on an unauthenticated API an unbounded row-creation vector.
    /// </summary>
    [Fact]
    public async Task Handle_WhenCallerIsNotSystemAdmin_ThrowsForbidden()
    {
        FakeCurrentUser caller = new();
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);

        ForbiddenException exception = await Assert.ThrowsAsync<ForbiddenException>(
            () => Handle(dbContext, caller, new CreateSchool.Command
            {
                Name = "Rideau Demo School",
                TimeZoneId = "America/Toronto"
            }));

        Assert.Equal(ErrorCodes.System.Forbidden, exception.ErrorCode);
        Assert.Empty(await dbContext.Schools.ToListAsync());
    }

    /// <summary>
    ///     New schools are active, and that is not negotiable from the payload. Accepting an
    ///     <c>isActive</c> flag on create would be a third route to an inactive school — and the one
    ///     that skips the privilege check by not being a transition.
    /// </summary>
    [Fact]
    public void Handle_DoesNotAcceptAnActiveFlag()
    {
        PropertyInfo[] properties = typeof(CreateSchool.Command).GetProperties(
            BindingFlags.Public | BindingFlags.Instance);

        Assert.DoesNotContain(properties, property =>
            property.Name.Contains("Active", StringComparison.OrdinalIgnoreCase));
    }

    private static Task<GetSchoolById.Response> Handle(
        SparkrockRwcDbContext dbContext,
        FakeCurrentUser currentUser,
        CreateSchool.Command command)
    {
        CreateSchool.CommandHandler handler = new(
            dbContext, currentUser, NullLogger<CreateSchool.CommandHandler>.Instance);

        return handler.Handle(command, CancellationToken.None);
    }
}
