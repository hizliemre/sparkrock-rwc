using domain.Security;

namespace features.tests.Security;

public sealed class AuditOverrideTests
{
    private static readonly Guid Actor = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void IsActive_BeforeBegin_IsFalse()
    {
        AuditOverride auditOverride = new();

        Assert.False(auditOverride.IsActive);
    }

    [Fact]
    public void Begin_SetsActingUserAndActivates()
    {
        AuditOverride auditOverride = new();

        using IDisposable scope = auditOverride.Begin(Actor);

        Assert.True(auditOverride.IsActive);
        Assert.Equal(Actor, auditOverride.ActingUserId);
    }

    [Fact]
    public void Begin_WhenScopeDisposed_Deactivates()
    {
        AuditOverride auditOverride = new();

        using (auditOverride.Begin(Actor))
        {
            Assert.True(auditOverride.IsActive);
        }

        Assert.False(auditOverride.IsActive);
    }

    /// <summary>
    ///     Nesting would make "who is the actor" ambiguous at the point the interceptor reads it.
    /// </summary>
    [Fact]
    public void Begin_WhenAlreadyActive_Throws()
    {
        AuditOverride auditOverride = new();

        using IDisposable scope = auditOverride.Begin(Actor);

        Assert.Throws<InvalidOperationException>(() => auditOverride.Begin(Actor));
    }

    [Fact]
    public void SystemImportUser_AsCurrentUser_IsSystemAdminWithEmptyScope()
    {
        ICurrentUser importUser = SystemImportUser.AsCurrentUser();

        Assert.Equal(SystemImportUser.Id, importUser.UserId);
        Assert.True(importUser.IsSystemAdmin);
        Assert.Empty(importUser.AuthorizedSchoolIds);
    }

    [Fact]
    public void SystemImportUser_IsDistinctFromTheEmptyGuid()
    {
        Assert.NotEqual(Guid.Empty, SystemImportUser.Id);
    }
}
