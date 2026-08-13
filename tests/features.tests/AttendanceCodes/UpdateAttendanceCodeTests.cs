using domain.AttendanceCodes;
using domain.Exceptions;
using features.AttendanceCodes;
using features.tests.Fakes;
using FluentValidation.Results;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace features.tests.AttendanceCodes;

public sealed class UpdateAttendanceCodeValidatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WhenValueIsAbsent_Fails(string? value)
    {
        ValidationFailure failure = Assert.Single(Validate(value: value!).Errors);

        Assert.Equal(nameof(UpdateAttendanceCode.Command.Value), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.RequiredField, failure.ErrorCode);
    }

    [Fact]
    public void Validate_WhenValueExceedsFiveCharacters_Fails() =>
        Assert.Equal(
            ErrorCodes.Validation.Failed,
            Assert.Single(Validate(value: "ABCDEF").Errors).ErrorCode);

    [Fact]
    public void Validate_WhenValueContainsWhitespace_Fails() =>
        Assert.Equal(ErrorCodes.Validation.Failed, Assert.Single(Validate(value: "A B").Errors).ErrorCode);

    [Fact]
    public void Validate_WhenDescriptionIsEmpty_Fails() =>
        Assert.Equal(
            ErrorCodes.Validation.RequiredField,
            Assert.Single(Validate(description: "").Errors).ErrorCode);

    [Fact]
    public void Validate_WhenDescriptionExceeds100_Fails() =>
        Assert.Equal(
            ErrorCodes.Validation.Failed,
            Assert.Single(Validate(description: new string('x', 101)).Errors).ErrorCode);

    /// <summary>
    ///     <c>PUT</c> is a replace, and an optional flag makes "absent" and "false" indistinguishable.
    /// </summary>
    [Fact]
    public void Validate_WhenIsActiveIsAbsent_Fails()
    {
        ValidationFailure failure = Assert.Single(Validate(isActive: null).Errors);

        Assert.Equal(nameof(UpdateAttendanceCode.Command.IsActive), failure.PropertyName);
        Assert.Equal(ErrorCodes.Validation.RequiredField, failure.ErrorCode);
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

    [Fact]
    public void Validate_WhenExcusedWithoutAbsent_Succeeds() =>
        Assert.True(Validate(isAbsent: false, isExcused: true).IsValid);

    private static ValidationResult Validate(
        string value = AttendanceCodeSeed.DefaultValue,
        string description = AttendanceCodeSeed.DefaultDescription,
        bool? isAbsent = true,
        bool? isExcused = false,
        bool? isActive = true)
    {
        UpdateAttendanceCode.CommandValidator validator = new();

        return validator.Validate(new UpdateAttendanceCode.Command
        {
            CodeId = Guid.NewGuid(),
            Value = value,
            Description = description,
            IsAbsent = isAbsent,
            IsExcused = isExcused,
            IsActive = isActive
        });
    }
}

public sealed class UpdateAttendanceCodeHandlerTests
{
    [Fact]
    public async Task Handle_UpdatesDescriptionAndFlags()
    {
        Guid codeId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await AttendanceCodeSeed.AddAsync(dbContext, codeId, value: "A", isAbsent: true, isExcused: false);

        GetAttendanceCodeById.Response response = await Handle(
            dbContext,
            codeId,
            description: "Absent (unexcused)",
            isAbsent: true,
            isExcused: true);

        AttendanceCode stored = await dbContext.AttendanceCodes.SingleAsync();

        Assert.Equal("Absent (unexcused)", stored.Description);
        Assert.True(stored.IsExcused);
        Assert.Equal("Absent (unexcused)", response.Description);
        Assert.True(response.IsExcused);
    }

    /// <summary>
    ///     A changed value would orphan the text already snapshotted into
    ///     <c>StudentAttendance.AttendCode</c> (D-02, V-23) — history would show <c>A</c> while the
    ///     code table showed something else, with nothing recording the rename.
    /// </summary>
    [Fact]
    public async Task Handle_WhenValueDiffersFromTheStoredOne_ThrowsBusinessRule()
    {
        Guid codeId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await AttendanceCodeSeed.AddAsync(dbContext, codeId, value: "A");

        BusinessRuleException exception = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handle(dbContext, codeId, value: "B"));

        Assert.Equal(ErrorCodes.AttendanceCode.ValueImmutable, exception.ErrorCode);

        Violation violation = Assert.Single(exception.Violations);

        Assert.Equal("body", violation.Source);
        Assert.Equal(nameof(UpdateAttendanceCode.Command.Value), violation.Path);
        Assert.Equal(ErrorCodes.AttendanceCode.ValueImmutable, violation.Code);
        Assert.Equal("A", (await dbContext.AttendanceCodes.SingleAsync()).Value);
    }

    /// <summary>
    ///     The rejected value is the caller's own bounded input and is safe to echo; the
    ///     <em>stored</em> one is not echoed, because that would let an unprivileged probe read it out
    ///     of the message. The admin check runs first, so no unprivileged caller reaches this message
    ///     at all — this asserts the message does not depend on it regardless.
    /// </summary>
    [Fact]
    public async Task Handle_WhenValueDiffers_DoesNotDiscloseTheStoredValue()
    {
        Guid codeId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await AttendanceCodeSeed.AddAsync(dbContext, codeId, value: "ZQX");

        BusinessRuleException exception = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handle(dbContext, codeId, value: "B"));

        Assert.DoesNotContain("ZQX", Assert.Single(exception.Violations).Message, StringComparison.Ordinal);
    }

    /// <summary>Comparison is against the normalised form, so <c>"a"</c> matches a stored <c>A</c>.</summary>
    [Fact]
    public async Task Handle_WhenValueDiffersOnlyByCase_Succeeds()
    {
        Guid codeId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await AttendanceCodeSeed.AddAsync(dbContext, codeId, value: "A");

        GetAttendanceCodeById.Response response = await Handle(
            dbContext,
            codeId,
            value: " a ",
            description: "Renamed");

        Assert.Equal("A", response.Value);
        Assert.Equal("Renamed", response.Description);
    }

    [Fact]
    public async Task Handle_WhenCodeDoesNotExist_ThrowsNotFound()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, Guid.NewGuid()));

        Assert.Equal(ErrorCodes.AttendanceCode.NotFound, exception.ErrorCode);
    }

    /// <summary><b>O-12's assertion.</b> The same transition, reached through the other verb.</summary>
    [Fact]
    public async Task Handle_WhenDeactivatingAndCallerIsNotSystemAdmin_ThrowsForbidden()
    {
        Guid codeId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await AttendanceCodeSeed.AddAsync(dbContext, codeId, isActive: true);

        ForbiddenException exception = await Assert.ThrowsAsync<ForbiddenException>(
            () => Handle(dbContext, codeId, isActive: false, currentUser: new FakeCurrentUser()));

        Assert.Equal(ErrorCodes.System.Forbidden, exception.ErrorCode);
        Assert.True((await dbContext.AttendanceCodes.SingleAsync()).IsActive);
    }

    /// <summary>
    ///     Both directions are guarded. Reactivation restores a code to the usable global namespace,
    ///     so treating the directions differently would leave the reactivating half of the same switch
    ///     unguarded.
    /// </summary>
    [Fact]
    public async Task Handle_WhenReactivatingAndCallerIsNotSystemAdmin_ThrowsForbidden()
    {
        Guid codeId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await AttendanceCodeSeed.AddAsync(dbContext, codeId, isActive: false);

        ForbiddenException exception = await Assert.ThrowsAsync<ForbiddenException>(
            () => Handle(dbContext, codeId, isActive: true, currentUser: new FakeCurrentUser()));

        Assert.Equal(ErrorCodes.System.Forbidden, exception.ErrorCode);
        Assert.False((await dbContext.AttendanceCodes.SingleAsync()).IsActive);
    }

    /// <summary>
    ///     Differs from F02 and F05 deliberately. Here <em>every</em> write is admin-only, not only the
    ///     transition, so an unchanged <c>isActive</c> does not rescue a non-admin edit — the value
    ///     namespace is global and permanent, and a description is what every school reads.
    /// </summary>
    [Fact]
    public async Task Handle_WhenCallerIsNotSystemAdminAndActivationIsUnchanged_ThrowsForbidden()
    {
        Guid codeId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await AttendanceCodeSeed.AddAsync(dbContext, codeId, description: "Original", isActive: true);

        ForbiddenException exception = await Assert.ThrowsAsync<ForbiddenException>(
            () => Handle(
                dbContext,
                codeId,
                description: "Rewritten",
                isActive: true,
                currentUser: FakeCurrentUser.ScopedTo(Guid.NewGuid())));

        Assert.Equal(ErrorCodes.System.Forbidden, exception.ErrorCode);
        Assert.Equal("Original", (await dbContext.AttendanceCodes.SingleAsync()).Description);
    }

    /// <summary>
    ///     The admin check precedes the value check, so an unprivileged caller cannot probe stored
    ///     values through the difference between a 400 and a 200.
    /// </summary>
    [Fact]
    public async Task Handle_WhenCallerIsNotSystemAdminAndValueAlsoMismatches_ThrowsForbiddenNotBusinessRule()
    {
        Guid codeId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await AttendanceCodeSeed.AddAsync(dbContext, codeId, value: "A");

        await Assert.ThrowsAsync<ForbiddenException>(
            () => Handle(dbContext, codeId, value: "B", currentUser: new FakeCurrentUser()));
    }

    /// <summary>
    ///     The privilege check must not become an existence oracle either: an unprivileged caller
    ///     naming an id that does not exist gets the same 404 an administrator would.
    /// </summary>
    [Fact]
    public async Task Handle_WhenCodeDoesNotExistAndCallerIsNotSystemAdmin_ThrowsNotFound()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, Guid.NewGuid(), currentUser: new FakeCurrentUser()));

        Assert.Equal(ErrorCodes.AttendanceCode.NotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WhenDeactivating_SetsIsActiveToFalse()
    {
        Guid codeId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await AttendanceCodeSeed.AddAsync(dbContext, codeId, isActive: true);

        GetAttendanceCodeById.Response response = await Handle(dbContext, codeId, isActive: false);

        Assert.False(response.IsActive);
        Assert.False((await dbContext.AttendanceCodes.SingleAsync()).IsActive);
    }

    /// <summary>Never hand-set an audit field (DEC-21) — advance the clock instead.</summary>
    [Fact]
    public async Task Handle_StampsModifiedAt()
    {
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();
        Guid codeId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock);
        await AttendanceCodeSeed.AddAsync(dbContext, codeId);

        clock.Advance(TimeSpan.FromHours(2));

        GetAttendanceCodeById.Response response = await Handle(dbContext, codeId, description: "Renamed");

        Assert.Equal(clock.GetUtcNow(), response.LastUpdatedAt);
        Assert.Equal(clock.GetUtcNow(), (await dbContext.AttendanceCodes.SingleAsync()).ModifiedAt);
    }

    /// <summary>
    ///     Conventions §3 and F02's shared artifact B: neither write slice contains its own
    ///     <c>IsActive</c> comparison. A second home for the rule is how O-12 came back the first time,
    ///     and prose cannot enforce its absence.
    /// </summary>
    [Theory]
    [InlineData("UpdateAttendanceCode.cs", "ActivationPolicy.ApplyReplacement")]
    [InlineData("DeactivateAttendanceCode.cs", "ActivationPolicy.Apply(")]
    public void Slice_ContainsNoLocalActivationComparison(string fileName, string expectedPolicyCall)
    {
        string source = Code(fileName);

        Assert.DoesNotContain("IsActive !=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IsActive ==", source, StringComparison.Ordinal);
        Assert.Contains(expectedPolicyCall, source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Comment lines are stripped before the scan. The prose in these slices quotes the comparison
    ///     it is explaining not to write, and a scan that matched it would fail on the documentation
    ///     rather than on the code — which trains the next reader to delete the documentation.
    /// </summary>
    private static string Code(string fileName)
    {
        string[] lines = File.ReadAllLines(Path.Combine(
            Architecture.SourceTree.Root().FullName,
            "src", "features", "AttendanceCodes", fileName));

        return string.Join(
            '\n',
            lines.Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }

    private static Task<GetAttendanceCodeById.Response> Handle(
        SparkrockRwcDbContext dbContext,
        Guid codeId,
        string value = AttendanceCodeSeed.DefaultValue,
        string description = AttendanceCodeSeed.DefaultDescription,
        bool isAbsent = true,
        bool isExcused = false,
        bool isActive = true,
        FakeCurrentUser? currentUser = null)
    {
        UpdateAttendanceCode.CommandHandler handler = new(
            dbContext,
            currentUser ?? FakeCurrentUser.SystemAdmin(),
            NullLogger<UpdateAttendanceCode.CommandHandler>.Instance);

        return handler.Handle(
            new UpdateAttendanceCode.Command
            {
                CodeId = codeId,
                Value = value,
                Description = description,
                IsAbsent = isAbsent,
                IsExcused = isExcused,
                IsActive = isActive
            },
            CancellationToken.None);
    }
}
