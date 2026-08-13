using domain.Exceptions;
using domain.Schools;
using features.Schools;
using features.tests.Fakes;
using FluentValidation.Results;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace features.tests.Schools;

public sealed class UpdateSchoolValidatorTests
{
    private readonly UpdateSchool.CommandValidator _validator = new();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WhenNameIsEmpty_Fails(string name)
    {
        ValidationResult result = _validator.Validate(Command(name: name));

        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(UpdateSchool.Command.Name), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.RequiredField, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenNameExceeds200_Fails()
    {
        ValidationResult result = _validator.Validate(Command(name: new string('a', 201)));

        Assert.Equal(ErrorCodes.Validation.Failed, Assert.Single(result.Errors).ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WhenTimeZoneIdIsEmpty_Fails(string timeZoneId)
    {
        ValidationResult result = _validator.Validate(Command(timeZoneId: timeZoneId));

        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(UpdateSchool.Command.TimeZoneId), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.RequiredField, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenTimeZoneIdExceeds64_Fails()
    {
        ValidationResult result = _validator.Validate(Command(timeZoneId: new string('a', 65)));

        Assert.Equal(ErrorCodes.Validation.Failed, Assert.Single(result.Errors).ErrorCode);
    }

    [Fact]
    public void Validate_WhenTimeZoneIdIsNotAKnownZone_Fails()
    {
        ValidationResult result = _validator.Validate(Command(timeZoneId: "Not/AZone"));

        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(UpdateSchool.Command.TimeZoneId), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
    }

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

        Assert.Equal(ErrorCodes.Validation.Failed, Assert.Single(result.Errors).ErrorCode);
    }

    [Fact]
    public void Validate_WhenThresholdIsAbsent_Succeeds()
    {
        Assert.True(_validator.Validate(Command(threshold: null)).IsValid);
    }

    /// <summary>
    ///     <c>PUT</c> is a replace. An optional flag makes "absent" and "false" indistinguishable, and
    ///     one of those two readings silently deactivates schools — which is why the property is
    ///     nullable and the validator, not the binder, is what rejects its absence.
    /// </summary>
    [Fact]
    public void Validate_WhenIsActiveIsAbsent_Fails()
    {
        ValidationResult result = _validator.Validate(Command(isActive: null));

        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(UpdateSchool.Command.IsActive), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.RequiredField, failure.ErrorCode);
    }

    private static UpdateSchool.Command Command(
        string name = SchoolSeed.DefaultName,
        string timeZoneId = SchoolSeed.DefaultTimeZoneId,
        int? threshold = 12,
        bool? isActive = true) =>
        new()
        {
            SchoolId = Guid.NewGuid(),
            Name = name,
            TimeZoneId = timeZoneId,
            AbsenceAlertThreshold = threshold,
            IsActive = isActive
        };
}

public sealed class UpdateSchoolHandlerTests
{
    [Fact]
    public async Task Handle_UpdatesEveryMutableField()
    {
        Guid schoolId = Guid.NewGuid();
        FakeCurrentUser admin = FakeCurrentUser.SystemAdmin();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: admin);
        await SchoolSeed.AddAsync(dbContext, schoolId, name: "Before", threshold: 12);

        GetSchoolById.Response response = await Handle(dbContext, admin, new UpdateSchool.Command
        {
            SchoolId = schoolId,
            Name = "After",
            TimeZoneId = "America/Vancouver",
            AbsenceAlertThreshold = 4,
            IsActive = false
        });

        School persisted = Assert.Single(await dbContext.Schools.ToListAsync());
        Assert.Equal("After", persisted.Name);
        Assert.Equal("America/Vancouver", persisted.TimeZoneId);
        Assert.Equal(4, persisted.AbsenceAlertThreshold);
        Assert.False(persisted.IsActive);

        Assert.Equal("After", response.Name);
        Assert.Equal("America/Vancouver", response.TimeZoneId);
        Assert.Equal(4, response.AbsenceAlertThreshold);
        Assert.Equal(4, response.EffectiveAbsenceAlertThreshold);
        Assert.False(response.IsActive);
    }

    [Fact]
    public async Task Handle_WhenSchoolDoesNotExist_ThrowsNotFound()
    {
        FakeCurrentUser admin = FakeCurrentUser.SystemAdmin();
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: admin);

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, admin, CommandFor(Guid.NewGuid())));

        Assert.Equal(ErrorCodes.School.NotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WhenSchoolIsOutsideScope_ThrowsNotFound()
    {
        Guid schoolId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(Guid.NewGuid());

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, caller, CommandFor(schoolId)));

        Assert.Equal(ErrorCodes.School.NotFound, exception.ErrorCode);
    }

    /// <summary>
    ///     O-12's assertion. <c>PUT { "isActive": false }</c> must fail exactly where <c>DELETE</c>
    ///     fails, or the privilege check is attached to an endpoint rather than to the transition.
    /// </summary>
    [Fact]
    public async Task Handle_WhenDeactivatingAndCallerIsNotSystemAdmin_ThrowsForbidden()
    {
        Guid schoolId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId, isActive: true);

        ForbiddenException exception = await Assert.ThrowsAsync<ForbiddenException>(
            () => Handle(dbContext, caller, CommandFor(schoolId, isActive: false)));

        Assert.Equal(ErrorCodes.System.Forbidden, exception.ErrorCode);
        Assert.True(Assert.Single(await dbContext.Schools.AsNoTracking().ToListAsync()).IsActive);
    }

    [Fact]
    public async Task Handle_WhenReactivatingAndCallerIsNotSystemAdmin_ThrowsForbidden()
    {
        Guid schoolId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId, isActive: false);

        ForbiddenException exception = await Assert.ThrowsAsync<ForbiddenException>(
            () => Handle(dbContext, caller, CommandFor(schoolId, isActive: true)));

        Assert.Equal(ErrorCodes.System.Forbidden, exception.ErrorCode);
        Assert.False(Assert.Single(await dbContext.Schools.AsNoTracking().ToListAsync()).IsActive);
    }

    /// <summary>
    ///     The privilege attaches to the <em>transition</em>, not to the endpoint. A non-admin renaming
    ///     a school in their own scope is allowed; if this test is red, the check was put on the wrong
    ///     thing.
    /// </summary>
    [Fact]
    public async Task Handle_WhenCallerIsNotSystemAdminAndActivationIsUnchanged_UpdatesTheOtherFields()
    {
        Guid schoolId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId, name: "Before", isActive: true);

        GetSchoolById.Response response = await Handle(dbContext, caller, new UpdateSchool.Command
        {
            SchoolId = schoolId,
            Name = "Renamed By A Non Admin",
            TimeZoneId = "America/Toronto",
            AbsenceAlertThreshold = 7,
            IsActive = true
        });

        Assert.Equal("Renamed By A Non Admin", response.Name);
        Assert.Equal(7, response.AbsenceAlertThreshold);
        Assert.True(response.IsActive);
        Assert.Equal("Renamed By A Non Admin", Assert.Single(await dbContext.Schools.ToListAsync()).Name);
    }

    /// <summary>
    ///     Audit fields are stamped by the interceptor and by nothing else (DEC-21), so the clock is
    ///     what a test controls.
    /// </summary>
    [Fact]
    public async Task Handle_StampsModifiedAt()
    {
        Guid schoolId = Guid.NewGuid();
        FakeCurrentUser admin = FakeCurrentUser.SystemAdmin();
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock, admin);
        School school = await SchoolSeed.AddAsync(dbContext, schoolId, name: "Before");
        Assert.Null(school.ModifiedAt);

        clock.Advance(TimeSpan.FromHours(2));
        DateTimeOffset modifiedAt = clock.GetUtcNow();

        GetSchoolById.Response response = await Handle(
            dbContext, admin, CommandFor(schoolId, name: "After"));

        Assert.Equal(modifiedAt, school.ModifiedAt);
        Assert.Equal(modifiedAt, response.LastUpdatedAt);
        Assert.NotEqual(modifiedAt, response.CreatedAt);
    }

    private static UpdateSchool.Command CommandFor(
        Guid schoolId,
        string name = SchoolSeed.DefaultName,
        bool? isActive = true) =>
        new()
        {
            SchoolId = schoolId,
            Name = name,
            TimeZoneId = SchoolSeed.DefaultTimeZoneId,
            AbsenceAlertThreshold = 12,
            IsActive = isActive
        };

    private static Task<GetSchoolById.Response> Handle(
        SparkrockRwcDbContext dbContext,
        FakeCurrentUser currentUser,
        UpdateSchool.Command command)
    {
        UpdateSchool.CommandHandler handler = new(
            dbContext, currentUser, NullLogger<UpdateSchool.CommandHandler>.Instance);

        return handler.Handle(command, CancellationToken.None);
    }
}
