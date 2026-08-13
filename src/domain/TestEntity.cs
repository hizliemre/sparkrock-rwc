using domain.Abstraction;

namespace domain;

/// <summary>
///     Scaffold reference entity, removed by F13.
/// </summary>
/// <remarks>
///     Kept on <see cref="SoftDeletableEntity" /> deliberately. Its two slices are currently the only
///     coverage over the reflective query filter, the audit interceptor and the in-memory factory, so
///     moving it to <see cref="BaseEntity" /> would delete that coverage during the exact window those
///     three mechanisms are being rewired. The later total-and-disjoint partition test must exempt it
///     by name.
/// </remarks>
public sealed class TestEntity : SoftDeletableEntity
{
    public required string TestProperty { get; set; }
}
