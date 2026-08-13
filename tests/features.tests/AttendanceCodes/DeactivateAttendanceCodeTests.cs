using domain.AttendanceCodes;
using domain.Exceptions;
using features.AttendanceCodes;
using features.tests.Fakes;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace features.tests.AttendanceCodes;

public sealed class DeactivateAttendanceCodeHandlerTests
{
    [Fact]
    public async Task Handle_SetsIsActiveToFalse()
    {
        Guid codeId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await AttendanceCodeSeed.AddAsync(dbContext, codeId);

        await Handle(dbContext, codeId);

        Assert.False((await dbContext.AttendanceCodes.SingleAsync()).IsActive);
    }

    /// <summary>
    ///     <c>Remove</c> on a <c>BaseEntity</c> throws in the audit interceptor (DEC-20), so this
    ///     catches a handler reaching for it — as a 500, which is what that mistake would ship as.
    /// </summary>
    [Fact]
    public async Task Handle_DoesNotRemoveTheRow()
    {
        Guid codeId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await AttendanceCodeSeed.AddAsync(dbContext, codeId);

        await Handle(dbContext, codeId);

        Assert.NotNull(await dbContext.AttendanceCodes.FirstOrDefaultAsync(code => code.Id == codeId));
    }

    /// <summary>
    ///     A no-op save stamps <c>ModifiedAt</c> through the interceptor and makes
    ///     <c>lastUpdatedAt</c> report a change that did not happen.
    /// </summary>
    [Fact]
    public async Task Handle_WhenAlreadyInactive_DoesNotWrite()
    {
        FakeTimeProvider clock = InMemoryDbContextFactory.Clock();
        Guid codeId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create(clock);
        await AttendanceCodeSeed.AddAsync(dbContext, codeId, isActive: false);

        clock.Advance(TimeSpan.FromHours(5));

        await Handle(dbContext, codeId);

        Assert.Null((await dbContext.AttendanceCodes.SingleAsync()).ModifiedAt);
    }

    [Fact]
    public async Task Handle_WhenCodeDoesNotExist_ThrowsNotFound()
    {
        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();

        NotFoundException exception = await Assert.ThrowsAsync<NotFoundException>(
            () => Handle(dbContext, Guid.NewGuid()));

        Assert.Equal(ErrorCodes.AttendanceCode.NotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task Handle_WhenCallerIsNotSystemAdmin_ThrowsForbidden()
    {
        Guid codeId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await AttendanceCodeSeed.AddAsync(dbContext, codeId);

        ForbiddenException exception = await Assert.ThrowsAsync<ForbiddenException>(
            () => Handle(dbContext, codeId, currentUser: FakeCurrentUser.ScopedTo(Guid.NewGuid())));

        Assert.Equal(ErrorCodes.System.Forbidden, exception.ErrorCode);
        Assert.True((await dbContext.AttendanceCodes.SingleAsync()).IsActive);
    }

    /// <summary>
    ///     The refusal does not depend on the row's current state, or the status is a state oracle:
    ///     204 for an already-inactive code and 403 for an active one would tell an unprivileged caller
    ///     which it was. <c>ActivationPolicy.Apply</c> checks privilege before the short-circuit
    ///     precisely so this holds.
    /// </summary>
    [Fact]
    public async Task Handle_WhenCallerIsNotSystemAdminAndCodeIsAlreadyInactive_ThrowsForbidden()
    {
        Guid codeId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await AttendanceCodeSeed.AddAsync(dbContext, codeId, isActive: false);

        ForbiddenException exception = await Assert.ThrowsAsync<ForbiddenException>(
            () => Handle(dbContext, codeId, currentUser: new FakeCurrentUser()));

        Assert.Equal(ErrorCodes.System.Forbidden, exception.ErrorCode);
    }

    /// <summary>
    ///     Both branches produce the identical failure, which is the property that makes the status
    ///     useless as a state oracle. Asserting the two separately would let one drift.
    /// </summary>
    [Fact]
    public async Task Handle_WhenCallerIsNotSystemAdmin_FailsIdenticallyWhateverTheCurrentState()
    {
        Guid active = Guid.NewGuid();
        Guid inactive = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await AttendanceCodeSeed.AddAsync(dbContext, active, value: "A");
        await AttendanceCodeSeed.AddAsync(dbContext, inactive, value: "X", isActive: false);

        FakeCurrentUser caller = new();

        ForbiddenException onActive = await Assert.ThrowsAsync<ForbiddenException>(
            () => Handle(dbContext, active, currentUser: caller));
        ForbiddenException onInactive = await Assert.ThrowsAsync<ForbiddenException>(
            () => Handle(dbContext, inactive, currentUser: caller));

        Assert.Equal(onActive.ErrorCode, onInactive.ErrorCode);
        Assert.Equal(onActive.Message, onInactive.Message);
    }

    /// <summary>
    ///     After deactivation the row is still there with the same <c>Value</c>. This is the
    ///     precondition for the behaviour the integration tier asserts — <c>ix_attendance_codes_value</c>
    ///     is unfiltered, so the value stays occupied and a re-<c>POST</c> is a 409 forever (F01c §6).
    /// </summary>
    [Fact]
    public async Task Handle_LeavesTheValueOccupied()
    {
        Guid codeId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await AttendanceCodeSeed.AddAsync(dbContext, codeId, value: "T");

        await Handle(dbContext, codeId);

        AttendanceCode stored = await dbContext.AttendanceCodes.SingleAsync();

        Assert.Equal("T", stored.Value);
        Assert.False(stored.IsActive);
    }

    /// <summary>
    ///     The aggregate has no tenant, so a caller with an empty scope who <em>is</em> an
    ///     administrator succeeds. A copied <c>EnsureAuthorized</c> would turn this into a 404.
    /// </summary>
    [Fact]
    public async Task Handle_AppliesNoTenantScope()
    {
        Guid codeId = Guid.NewGuid();

        await using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        await AttendanceCodeSeed.AddAsync(dbContext, codeId);

        await Handle(dbContext, codeId, currentUser: FakeCurrentUser.SystemAdmin());

        Assert.False((await dbContext.AttendanceCodes.SingleAsync()).IsActive);
    }

    private static Task Handle(
        SparkrockRwcDbContext dbContext,
        Guid codeId,
        FakeCurrentUser? currentUser = null)
    {
        DeactivateAttendanceCode.CommandHandler handler = new(
            dbContext,
            currentUser ?? FakeCurrentUser.SystemAdmin(),
            NullLogger<DeactivateAttendanceCode.CommandHandler>.Instance);

        return handler.Handle(new DeactivateAttendanceCode.Command { CodeId = codeId }, CancellationToken.None);
    }
}
