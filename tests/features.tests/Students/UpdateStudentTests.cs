using domain.Exceptions;
using domain.Students;
using features.Students;
using features.tests.Fakes;
using features.tests.Schools;
using FluentValidation.Results;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace features.tests.Students;

public sealed class UpdateStudentValidatorTests
{
    private readonly UpdateStudent.CommandValidator _validator = new();

    [Fact]
    public void Validate_WhenFirstNameIsEmpty_Fails()
    {
        ValidationResult result = _validator.Validate(Command(firstName: string.Empty));

        Assert.False(result.IsValid);
        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(UpdateStudent.Command.FirstName), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.RequiredField, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenFirstNameExceeds100_Fails()
    {
        ValidationResult result = _validator.Validate(
            Command(firstName: new string('a', CreateStudent.MaxNameLength + 1)));

        Assert.False(result.IsValid);
        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(UpdateStudent.Command.FirstName), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenLastNameIsEmpty_Fails()
    {
        ValidationResult result = _validator.Validate(Command(lastName: string.Empty));

        Assert.False(result.IsValid);
        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(UpdateStudent.Command.LastName), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.RequiredField, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenLastNameExceeds100_Fails()
    {
        ValidationResult result = _validator.Validate(
            Command(lastName: new string('a', CreateStudent.MaxNameLength + 1)));

        Assert.False(result.IsValid);
        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(UpdateStudent.Command.LastName), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenGradeExceeds10_Fails()
    {
        ValidationResult result = _validator.Validate(
            Command(grade: new string('9', CreateStudent.MaxGradeLength + 1)));

        Assert.False(result.IsValid);
        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(UpdateStudent.Command.Grade), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.Failed, failure.ErrorCode);
    }

    /// <summary>
    ///     <c>PUT</c> is a replace. An optional flag makes "absent" and "false" indistinguishable, so
    ///     every partial update sent by a client that did not know about the flag would silently
    ///     deactivate the student.
    /// </summary>
    [Fact]
    public void Validate_WhenIsActiveIsAbsent_Fails()
    {
        ValidationResult result = _validator.Validate(Command(isActive: null));

        Assert.False(result.IsValid);
        ValidationFailure failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(UpdateStudent.Command.IsActive), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.RequiredField, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenGradeIsAbsent_Succeeds()
    {
        ValidationResult result = _validator.Validate(Command(grade: null));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("Siobhán", "O'Brien")]
    [InlineData("Mary Jane", "Smith-Jones")]
    [InlineData("Zoë", "Đurić")]
    public void Validate_WhenNameContainsApostrophesHyphensOrDiacritics_Succeeds(string firstName, string lastName)
    {
        ValidationResult result = _validator.Validate(Command(firstName: firstName, lastName: lastName));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    private static UpdateStudent.Command Command(
        string firstName = StudentSeed.DefaultFirstName,
        string lastName = StudentSeed.DefaultLastName,
        string? grade = StudentSeed.DefaultGrade,
        bool? isActive = true) => new()
    {
        SchoolId = Guid.NewGuid(),
        StudentId = Guid.NewGuid(),
        FirstName = firstName,
        LastName = lastName,
        Grade = grade,
        IsActive = isActive
    };
}

public sealed class UpdateStudentHandlerTests
{
    [Fact]
    public async Task Handle_UpdatesNamesAndGrade()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId, "Demo", "Student33", "09");

        GetStudentById.Response response = await Handle(
            dbContext, caller, Command(schoolId, studentId, firstName: "Renamed", lastName: "Surname", grade: "10"));

        Student persisted = Assert.Single(await dbContext.Students.AsNoTracking().ToListAsync());
        Assert.Equal("Renamed", persisted.FirstName);
        Assert.Equal("Surname", persisted.LastName);
        Assert.Equal("10", persisted.Grade);
        Assert.Equal("Renamed", response.FirstName);
        Assert.Equal("Surname", response.LastName);
        Assert.Equal("10", response.Grade);
    }

    /// <summary>
    ///     Replace semantics. Surprising, documented, and the only way to clear a grade at all — the
    ///     alternative, treating absent as "keep", makes <c>PUT</c> a partial update.
    /// </summary>
    [Fact]
    public async Task Handle_WhenGradeIsOmitted_ClearsIt()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId, grade: "09");

        GetStudentById.Response response = await Handle(
            dbContext, caller, Command(schoolId, studentId, grade: null));

        Assert.Null(Assert.Single(await dbContext.Students.AsNoTracking().ToListAsync()).Grade);
        Assert.Null(response.Grade);
    }

    /// <summary>
    ///     <b>There is no transfer.</b> The request model has no <c>SchoolId</c> member of its own:
    ///     DEC-08 gives <c>Student</c> a single mutable FK with no enrolment history, V-13 makes a
    ///     back-dated correction for the former school fail, and DEC-16 makes the former school lose
    ///     access to rows it recorded. Exposing that through an ordinary edit is worse than not
    ///     exposing it.
    /// </summary>
    [Fact]
    public async Task Handle_CannotChangeTheSchool()
    {
        Guid schoolId = Guid.NewGuid();
        Guid otherSchoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId, otherSchoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId, name: "Origin");
        await SchoolSeed.AddAsync(dbContext, otherSchoolId, name: "Destination");
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        GetStudentById.Response response = await Handle(dbContext, caller, Command(schoolId, studentId));

        Assert.Equal(schoolId, Assert.Single(await dbContext.Students.AsNoTracking().ToListAsync()).SchoolId);
        Assert.Equal(schoolId, response.SchoolId);

        Assert.DoesNotContain(
            typeof(UpdateStudent.Body).GetProperties(),
            property => property.Name.Contains("School", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Handle_WhenStudentDoesNotExist_ThrowsNotFound()
    {
        Guid schoolId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, caller, Command(schoolId, Guid.NewGuid())));

        Assert.Equal(ErrorCodes.Student.NotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WhenStudentBelongsToAnotherSchool_ThrowsNotFound()
    {
        Guid addressedSchoolId = Guid.NewGuid();
        Guid otherSchoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(addressedSchoolId, otherSchoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, addressedSchoolId, name: "Addressed");
        await SchoolSeed.AddAsync(dbContext, otherSchoolId, name: "Other");
        await StudentSeed.AddAsync(dbContext, otherSchoolId, studentId, firstName: "Untouched");

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, caller, Command(addressedSchoolId, studentId, firstName: "Hijacked")));

        Assert.Equal(ErrorCodes.Student.NotFound, exception.ErrorCode);
        Assert.Equal("Untouched", Assert.Single(await dbContext.Students.AsNoTracking().ToListAsync()).FirstName);
    }

    [Fact]
    public async Task Handle_WhenSchoolIsOutsideScope_ThrowsNotFound()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(Guid.NewGuid());

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId, firstName: "Untouched");

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, caller, Command(schoolId, studentId, firstName: "Hijacked")));

        Assert.Equal(ErrorCodes.School.NotFound, exception.ErrorCode);
        Assert.Equal("Untouched", Assert.Single(await dbContext.Students.AsNoTracking().ToListAsync()).FirstName);
    }

    /// <summary>
    ///     No 403 in F05: DEC-20 requires school scope, and no more, to deactivate a student. This is
    ///     O-12's shape without a privilege, and it must reach the same state as <c>DELETE</c>.
    /// </summary>
    [Fact]
    public async Task Handle_WhenDeactivatingViaPut_SucceedsForAnInScopeCaller()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId);

        GetStudentById.Response response = await Handle(
            dbContext, caller, Command(schoolId, studentId, isActive: false));

        Assert.False(Assert.Single(await dbContext.Students.AsNoTracking().ToListAsync()).IsActive);
        Assert.False(response.IsActive);
    }

    [Fact]
    public async Task Handle_WhenReactivatingViaPut_SucceedsForAnInScopeCaller()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId, isActive: false);

        GetStudentById.Response response = await Handle(
            dbContext, caller, Command(schoolId, studentId, isActive: true));

        Assert.True(Assert.Single(await dbContext.Students.AsNoTracking().ToListAsync()).IsActive);
        Assert.True(response.IsActive);
    }

    /// <summary>
    ///     Stops a 403 being copied in from the F02 or F03 slice next door. F05 declares no 403 on any
    ///     route, so producing one would make the OpenAPI catalogue wrong as well as the behaviour.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handle_NeverThrowsForbidden(bool isActive)
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);

        Assert.False(caller.IsSystemAdmin);

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(currentUser: caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        await StudentSeed.AddAsync(dbContext, schoolId, studentId, isActive: !isActive);

        GetStudentById.Response response = await Handle(
            dbContext, caller, Command(schoolId, studentId, isActive: isActive));

        Assert.Equal(isActive, response.IsActive);
    }

    /// <summary>
    ///     DEC-21: audit fields are never hand-set. The clock is advanced and the interceptor stamps.
    /// </summary>
    [Fact]
    public async Task Handle_StampsModifiedAt()
    {
        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();
        FakeCurrentUser caller = FakeCurrentUser.ScopedTo(schoolId);
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock, caller);
        await SchoolSeed.AddAsync(dbContext, schoolId);
        Student seeded = await StudentSeed.AddAsync(dbContext, schoolId, studentId);
        Assert.Null(seeded.ModifiedAt);

        clock.Advance(TimeSpan.FromHours(4));
        DateTimeOffset modifiedAt = clock.GetUtcNow();

        GetStudentById.Response response = await Handle(
            dbContext, caller, Command(schoolId, studentId, firstName: "Renamed"));

        Assert.Equal(modifiedAt, Assert.Single(await dbContext.Students.AsNoTracking().ToListAsync()).ModifiedAt);
        Assert.Equal(modifiedAt, response.LastUpdatedAt);
        Assert.NotEqual(modifiedAt, response.CreatedAt);
    }

    private static UpdateStudent.Command Command(
        Guid schoolId,
        Guid studentId,
        string firstName = "Demo",
        string lastName = "Student33",
        string? grade = "09",
        bool? isActive = true) => new()
    {
        SchoolId = schoolId,
        StudentId = studentId,
        FirstName = firstName,
        LastName = lastName,
        Grade = grade,
        IsActive = isActive
    };

    private static Task<GetStudentById.Response> Handle(
        SparkrockRwcDbContext dbContext,
        FakeCurrentUser currentUser,
        UpdateStudent.Command command)
    {
        UpdateStudent.CommandHandler handler = new(
            dbContext, currentUser, NullLogger<UpdateStudent.CommandHandler>.Instance);

        return handler.Handle(command, CancellationToken.None);
    }
}
