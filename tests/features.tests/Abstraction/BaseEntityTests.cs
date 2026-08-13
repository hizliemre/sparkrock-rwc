using System.Reflection;
using domain;
using domain.Abstraction;

namespace features.tests.Abstraction;

public sealed class BaseEntityTests
{
    private static readonly string[] SoftDeleteMembers = ["IsDeleted", "DeletedAt", "DeletedBy"];

    /// <summary>
    ///     DEC-20: reference entities must not carry soft-delete columns at all. A column that exists
    ///     but is never maintained is a partial index waiting to break and an <c>INNER JOIN</c> the
    ///     query filter emits regardless.
    /// </summary>
    [Theory]
    [InlineData("IsDeleted")]
    [InlineData("DeletedAt")]
    [InlineData("DeletedBy")]
    public void BaseEntity_DeclaresNoSoftDeleteMember(string memberName)
    {
        Assert.Null(typeof(BaseEntity).GetProperty(memberName));
    }

    [Theory]
    [InlineData("IsDeleted")]
    [InlineData("DeletedAt")]
    [InlineData("DeletedBy")]
    public void IAuditableEntity_DeclaresNoSoftDeleteMember(string memberName)
    {
        Assert.Null(typeof(IAuditableEntity).GetProperty(memberName));
    }

    [Fact]
    public void SoftDeletableEntity_DerivesFromBaseEntity()
    {
        Assert.True(typeof(BaseEntity).IsAssignableFrom(typeof(SoftDeletableEntity)));
    }

    [Fact]
    public void SoftDeletableEntity_DeclaresEverySoftDeleteMember()
    {
        foreach (string memberName in SoftDeleteMembers)
            Assert.NotNull(typeof(SoftDeletableEntity).GetProperty(memberName));
    }

    /// <summary>
    ///     DEC-21: the interceptor is the only writer of audit attribution, enforced by the type
    ///     system rather than by review.
    /// </summary>
    /// <remarks>
    ///     Three review rounds left this unenforced while two decisions came to depend on it. A
    ///     public setter here means any of twelve parallel workstreams can assign CreatedBy and the
    ///     audit trail silently becomes fiction.
    /// </remarks>
    [Theory]
    [InlineData(typeof(BaseEntity), "CreatedAt")]
    [InlineData(typeof(BaseEntity), "CreatedBy")]
    [InlineData(typeof(BaseEntity), "ModifiedAt")]
    [InlineData(typeof(BaseEntity), "ModifiedBy")]
    [InlineData(typeof(SoftDeletableEntity), "IsDeleted")]
    [InlineData(typeof(SoftDeletableEntity), "DeletedAt")]
    [InlineData(typeof(SoftDeletableEntity), "DeletedBy")]
    public void AuditMembers_HaveNoPublicSetter(Type declaringType, string memberName)
    {
        PropertyInfo property = declaringType.GetProperty(memberName)!;

        Assert.NotNull(property);
        Assert.Null(property.GetSetMethod(nonPublic: false));
    }

    /// <summary>
    ///     Id is not an audit field and the import assigns it, so it keeps a public setter.
    /// </summary>
    [Fact]
    public void Id_KeepsItsPublicSetter()
    {
        Assert.NotNull(typeof(BaseEntity).GetProperty("Id")!.GetSetMethod(nonPublic: false));
    }

    /// <summary>
    ///     Kept soft-deletable on purpose while its slices are the only regression net over the
    ///     reflective loop, the interceptor and the InMemory factory. F13 removes it.
    /// </summary>
    [Fact]
    public void TestEntity_IsSoftDeletableWhileItRemainsTheReferenceSlice()
    {
        Assert.True(typeof(SoftDeletableEntity).IsAssignableFrom(typeof(TestEntity)));
    }
}
