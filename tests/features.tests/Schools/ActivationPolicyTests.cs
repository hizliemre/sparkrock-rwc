using domain.Abstraction;
using domain.Exceptions;
using domain.Security;
using features.tests.Fakes;

namespace features.tests.Schools;

/// <summary>
///     The shared activation guard (DEC-20, O-12), authored by F02 and consumed by F03, F04 and F05.
/// </summary>
/// <remarks>
///     Lives beside the Schools slice tests rather than in <c>features.tests/Security/</c> because
///     F02's working scope covers <c>tests/features.tests/Schools/</c> only. The subject is shared;
///     the location is not a statement about ownership.
/// </remarks>
public sealed class ActivationPolicyTests
{
    [Fact]
    public void Apply_WhenStateAlreadyMatches_ReturnsFalseAndDoesNotChangeTheEntity()
    {
        ActivatableRow entity = new() { IsActive = true };

        bool changed = ActivationPolicy.Apply(
            entity, requestedIsActive: true, FakeCurrentUser.SystemAdmin(), ActivationPrivilege.SystemAdmin, "School");

        Assert.False(changed);
        Assert.True(entity.IsActive);
    }

    [Fact]
    public void Apply_WhenStateChanges_ReturnsTrueAndAssignsIt()
    {
        ActivatableRow entity = new() { IsActive = true };

        bool changed = ActivationPolicy.Apply(
            entity, requestedIsActive: false, FakeCurrentUser.SystemAdmin(), ActivationPrivilege.SystemAdmin, "School");

        Assert.True(changed);
        Assert.False(entity.IsActive);
    }

    [Fact]
    public void Apply_WhenSystemAdminPrivilegeRequiredAndCallerIsNotAdmin_ThrowsForbidden()
    {
        ActivatableRow entity = new() { IsActive = true };

        ForbiddenException exception = Assert.Throws<ForbiddenException>(() => ActivationPolicy.Apply(
            entity, requestedIsActive: false, new FakeCurrentUser(), ActivationPrivilege.SystemAdmin, "School"));

        Assert.Equal(ErrorCodes.System.Forbidden, exception.ErrorCode);
        Assert.True(entity.IsActive);
    }

    [Fact]
    public void Apply_WhenSystemAdminPrivilegeRequiredAndCallerIsAdmin_Applies()
    {
        ActivatableRow entity = new() { IsActive = true };

        bool changed = ActivationPolicy.Apply(
            entity, requestedIsActive: false, FakeCurrentUser.SystemAdmin(), ActivationPrivilege.SystemAdmin, "School");

        Assert.True(changed);
        Assert.False(entity.IsActive);
    }

    /// <summary>
    ///     <see cref="ActivationPrivilege.SchoolScope" /> performs no check of its own: the caller has
    ///     already passed <c>EnsureAuthorized</c> at load, which is DEC-20's requirement for a Student.
    /// </summary>
    [Fact]
    public void Apply_WhenSchoolScopePrivilege_AppliesWithoutAnAdminCheck()
    {
        ActivatableRow entity = new() { IsActive = true };

        bool changed = ActivationPolicy.Apply(
            entity, requestedIsActive: false, new FakeCurrentUser(), ActivationPrivilege.SchoolScope, "Student");

        Assert.True(changed);
        Assert.False(entity.IsActive);
    }

    /// <summary>
    ///     The ordering test. Privilege is checked <b>before</b> the already-in-that-state
    ///     short-circuit.
    /// </summary>
    /// <remarks>
    ///     The reverse order makes the response depend on the row's current state — 204 for an already
    ///     inactive school, 403 for an active one — which turns the status code into a state oracle for
    ///     an unprivileged caller. The cost is that an unprivileged <c>DELETE</c> on an already
    ///     inactive school is a 403 rather than an idempotent 204; that is the correct trade. Anyone
    ///     "simplifying" the function by moving the early return up fails exactly this test.
    /// </remarks>
    [Fact]
    public void Apply_WhenCallerLacksPrivilegeAndStateAlreadyMatches_ThrowsForbidden()
    {
        ActivatableRow entity = new() { IsActive = false };

        ForbiddenException exception = Assert.Throws<ForbiddenException>(() => ActivationPolicy.Apply(
            entity, requestedIsActive: false, new FakeCurrentUser(), ActivationPrivilege.SystemAdmin, "School"));

        Assert.Equal(ErrorCodes.System.Forbidden, exception.ErrorCode);
    }

    /// <summary>
    ///     O-12 with the sign flipped. Reactivation restores a school to accepting submissions and a
    ///     code to the usable global namespace, so guarding only deactivation leaves half the switch
    ///     unguarded.
    /// </summary>
    [Fact]
    public void Apply_WhenReactivating_AppliesTheSamePrivilegeCheck()
    {
        ActivatableRow entity = new() { IsActive = false };

        ForbiddenException exception = Assert.Throws<ForbiddenException>(() => ActivationPolicy.Apply(
            entity, requestedIsActive: true, new FakeCurrentUser(), ActivationPrivilege.SystemAdmin, "School"));

        Assert.Equal(ErrorCodes.System.Forbidden, exception.ErrorCode);
        Assert.False(entity.IsActive);
    }

    /// <summary>
    ///     The replace form. A <c>PUT</c> body restating the flag the row already carries is not an
    ///     exercise of the privilege, so an unprivileged caller may still change the other fields.
    /// </summary>
    [Fact]
    public void ApplyReplacement_WhenStateAlreadyMatchesAndCallerLacksPrivilege_ReturnsFalse()
    {
        ActivatableRow entity = new() { IsActive = true };

        bool changed = ActivationPolicy.ApplyReplacement(
            entity, requestedIsActive: true, new FakeCurrentUser(), ActivationPrivilege.SystemAdmin, "School");

        Assert.False(changed);
        Assert.True(entity.IsActive);
    }

    /// <summary>O-12's assertion: the replace form guards a real transition exactly as hard.</summary>
    [Fact]
    public void ApplyReplacement_WhenDeactivatingAndCallerLacksPrivilege_ThrowsForbidden()
    {
        ActivatableRow entity = new() { IsActive = true };

        ForbiddenException exception = Assert.Throws<ForbiddenException>(() => ActivationPolicy.ApplyReplacement(
            entity, requestedIsActive: false, new FakeCurrentUser(), ActivationPrivilege.SystemAdmin, "School"));

        Assert.Equal(ErrorCodes.System.Forbidden, exception.ErrorCode);
        Assert.True(entity.IsActive);
    }

    [Fact]
    public void ApplyReplacement_WhenReactivatingAndCallerLacksPrivilege_ThrowsForbidden()
    {
        ActivatableRow entity = new() { IsActive = false };

        ForbiddenException exception = Assert.Throws<ForbiddenException>(() => ActivationPolicy.ApplyReplacement(
            entity, requestedIsActive: true, new FakeCurrentUser(), ActivationPrivilege.SystemAdmin, "School"));

        Assert.Equal(ErrorCodes.System.Forbidden, exception.ErrorCode);
        Assert.False(entity.IsActive);
    }

    [Fact]
    public void ApplyReplacement_WhenStateChangesAndCallerIsAdmin_AppliesIt()
    {
        ActivatableRow entity = new() { IsActive = true };

        bool changed = ActivationPolicy.ApplyReplacement(
            entity, requestedIsActive: false, FakeCurrentUser.SystemAdmin(), ActivationPrivilege.SystemAdmin, "School");

        Assert.True(changed);
        Assert.False(entity.IsActive);
    }

    [Fact]
    public void Apply_WhenResourceNameIsGiven_NamesItInTheMessage()
    {
        ActivatableRow entity = new() { IsActive = true };

        ForbiddenException exception = Assert.Throws<ForbiddenException>(() => ActivationPolicy.Apply(
            entity, requestedIsActive: false, new FakeCurrentUser(), ActivationPrivilege.SystemAdmin, "AttendanceCode"));

        Assert.Contains("AttendanceCode", exception.Message, StringComparison.Ordinal);
    }
}

/// <summary>
///     Minimal <see cref="IActivatable" /> stand-in. The policy is written against the interface, so
///     exercising it through a concrete aggregate would test the aggregate as well.
/// </summary>
internal sealed class ActivatableRow : IActivatable
{
    public bool IsActive { get; set; }
}
