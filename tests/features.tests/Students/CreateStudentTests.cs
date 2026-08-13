using domain.Exceptions;
using domain.Students;
using features.Students;
using features.tests.Fakes;
using features.tests.Schools;
using FluentValidation.Results;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace features.tests.Students;

public sealed class CreateStudentValidatorTests
{
    private readonly CreateStudent.CommandValidator _validator = new();

    [Fact]
    public void Validate_WhenFirstNameIsEmpty_Fails()
    {
        ValidationResult result = _validator.Validate(Command(firstName: string.Empty));

        Assert.False(result.IsValid);
        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(CreateStudent.Command.FirstName), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.RequiredField, failure.ErrorCode);
    }

    /// <summary>Mirrors F01c §3's <c>varchar(100)</c> exactly.</summary>
    [Fact]
    public void Validate_WhenFirstNameExceeds100_Fails()
    {
        ValidationResult result = _validator.Validate(
            Command(firstName: new string('a', CreateStudent.MaxNameLength + 1)));

        Assert.False(result.IsValid);
        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(CreateStudent.Command.FirstName), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenLastNameIsEmpty_Fails()
    {
        ValidationResult result = _validator.Validate(Command(lastName: string.Empty));

        Assert.False(result.IsValid);
        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(CreateStudent.Command.LastName), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.RequiredField, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenLastNameExceeds100_Fails()
    {
        ValidationResult result = _validator.Validate(
            Command(lastName: new string('a', CreateStudent.MaxNameLength + 1)));

        Assert.False(result.IsValid);
        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(CreateStudent.Command.LastName), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenGradeExceeds10_Fails()
    {
        ValidationResult result = _validator.Validate(
            Command(grade: new string('9', CreateStudent.MaxGradeLength + 1)));

        Assert.False(result.IsValid);
        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(CreateStudent.Command.Grade), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
    }

    /// <summary>
    ///     Nullable in the schema, and the case that raised L-15's runtime error 94.
    /// </summary>
    [Fact]
    public void Validate_WhenGradeIsAbsent_Succeeds()
    {
        ValidationResult result = _validator.Validate(Command(grade: null));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    /// <summary>
    ///     There is no format rule on names, and this test exists to stop one being added. Names carry
    ///     apostrophes, hyphens, spaces, diacritics and scripts a validator author will not anticipate,
    ///     and a rejected legitimate name is a child who cannot be marked present.
    /// </summary>
    [Theory]
    [InlineData("Siobhán", "O'Brien")]
    [InlineData("Mary Jane", "Smith-Jones")]
    [InlineData("Zoë", "Đurić")]
    [InlineData("美玲", "陳")]
    public void Validate_WhenNameContainsApostrophesHyphensOrDiacritics_Succeeds(string firstName, string lastName)
    {
        ValidationResult result = _validator.Validate(Command(firstName: firstName, lastName: lastName));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    private static CreateStudent.Command Command(
        string firstName = StudentSeed.DefaultFirstName,
        string lastName = StudentSeed.DefaultLastName,
        string? grade = StudentSeed.DefaultGrade) => new()
    {
        SchoolId = Guid.NewGuid(),
        FirstName = firstName,
        LastName = lastName,
        Grade = grade
    };
}

public sealed class CreateStudentHandlerTests
{
    [Fact]
    public async Task Handle_PersistsTheStudentInTheAddressedSchool()
    {
        Guid schoolId = Guid.NewGuid();
        Guid otherSchoolId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId, otherSchoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId, name: "Addressed");
        await SchoolSeed.AddAsync(dbContext, otherSchoolId, name: "Other");

        await Handle(dbContext, caller, Command(schoolId));

        Student persisted = Assert.Single(await dbContext.Students.AsNoTracking().ToListAsync());
        Assert.Equal(schoolId, persisted.SchoolId);
        Assert.Equal("Demo", persisted.FirstName);
        Assert.Equal("Student33", persisted.LastName);
        Assert.Equal("09", persisted.Grade);
    }

    [Fact]
    public async Task Handle_PersistsTheStudentAsActive()
    {
        Guid schoolId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);

        await Handle(dbContext, caller, Command(schoolId));

        Assert.True(Assert.Single(await dbContext.Students.AsNoTracking().ToListAsync()).IsActive);
    }

    [Fact]
    public async Task Handle_ReturnsTheCreatedResponse()
    {
        Guid schoolId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);

        GetStudentById.Response response = await Handle(dbContext, caller, Command(schoolId));

        Student persisted = Assert.Single(await dbContext.Students.AsNoTracking().ToListAsync());
        Assert.Equal(persisted.Id, response.Id);
        Assert.NotEqual(Guid.Empty, response.Id);
        Assert.Equal(schoolId, response.SchoolId);
        Assert.Equal("Demo", response.FirstName);
        Assert.Equal("Student33", response.LastName);
        Assert.Equal("09", response.Grade);
        Assert.True(response.IsActive);
        Assert.Equal(InMemoryDbContextFactory.DefaultNow, response.CreatedAt);
        Assert.Equal(InMemoryDbContextFactory.DefaultNow, response.LastUpdatedAt);
    }

    /// <summary>
    ///     Before the insert, so <c>STUDENT.REFERENCE_MISSING</c> stays unreachable outside a race.
    ///     The caller is an administrator, so the scope check cannot be what produces the 404.
    /// </summary>
    [Fact]
    public async Task Handle_WhenSchoolDoesNotExist_ThrowsNotFound()
    {
        FakeCurrentUser admin = FakeCurrentUser.SystemAdmin();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: admin);

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, admin, Command(Guid.NewGuid())));

        Assert.Equal(ErrorCodes.School.NotFound, exception.ErrorCode);
        Assert.Empty(await dbContext.Students.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Handle_WhenSchoolIsOutsideScope_ThrowsNotFound()
    {
        Guid schoolId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(Guid.NewGuid());

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, caller, Command(schoolId)));

        Assert.Equal(ErrorCodes.School.NotFound, exception.ErrorCode);
        Assert.Empty(await dbContext.Students.AsNoTracking().ToListAsync());
    }

    /// <summary>Route values are authoritative; the body must not repeat <c>schoolId</c>.</summary>
    [Fact]
    public void Body_DeclaresNoSchoolIdMember()
    {
        Assert.DoesNotContain(
            typeof(CreateStudent.Body).GetProperties(),
            property => property.Name.Contains("School", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     New students are active. Accepting the flag here would be a path to an inactive student
    ///     that is not a transition, and therefore bypasses <c>ActivationPolicy</c> entirely (O-12).
    /// </summary>
    [Fact]
    public void Command_DeclaresNoActiveFlag()
    {
        Assert.DoesNotContain(
            typeof(CreateStudent.Command).GetProperties(),
            property => property.Name.Contains("Active", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(
            typeof(CreateStudent.Body).GetProperties(),
            property => property.Name.Contains("Active", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     No uniqueness on names. F01c declined it, because a real school has two children with the
    ///     same name and inventing a natural key makes the import reject legitimate rows.
    /// </summary>
    [Fact]
    public async Task Handle_WhenAnotherStudentHasTheSameName_Succeeds()
    {
        Guid schoolId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, firstName: "Demo", lastName: "Student33");

        GetStudentById.Response response = await Handle(dbContext, caller, Command(schoolId));

        Assert.Equal(2, (await dbContext.Students.AsNoTracking().ToListAsync()).Count);
        Assert.Equal("Student33", response.LastName);
    }

    private static CreateStudent.Command Command(Guid schoolId) => new()
    {
        SchoolId = schoolId,
        FirstName = "Demo",
        LastName = "Student33",
        Grade = "09"
    };

    private static Task<GetStudentById.Response> Handle(
        SparkrockRwcDbContext dbContext,
        FakeCurrentUser currentUser,
        CreateStudent.Command command)
    {
        CreateStudent.CommandHandler handler = new(
            dbContext, currentUser, NullLogger<CreateStudent.CommandHandler>.Instance);

        return handler.Handle(command, CancellationToken.None);
    }
}
