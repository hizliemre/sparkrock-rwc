using domain.Abstraction;

namespace domain;

/// <summary>
///     Scaffold reference entity. Permanent — F13, which would have removed it, is cancelled.
/// </summary>
/// <remarks>
///     <para>
///         On <see cref="SoftDeletableEntity" /> deliberately. Its two slices are the coverage over
///         the reflective query filter, the audit interceptor and the in-memory factory that does not
///         depend on any business feature, so it keeps testing those mechanisms in isolation from
///         whatever the attendance model happens to look like.
///     </para>
///     <para>
///         It needs <b>no exemption</b> from DEC-20's partition test, contrary to what F01a's
///         specification anticipated. The partition rule is that a query filter is present exactly
///         when the entity is soft-deletable, and this type satisfies it: it derives from
///         <see cref="SoftDeletableEntity" /> and the reflective loop gives it a filter. The
///         exemption was only ever needed for a type that sat on the wrong side of the rule, and this
///         one does not — <c>LifecyclePartitionTests</c> sweeps every entity and names none.
///     </para>
///     <para>
///         It is not the reference slice. It predates most of the conventions, and F02 is nominated
///         for CRUD, F07 for the transactional shape.
///     </para>
/// </remarks>
public sealed class TestEntity : SoftDeletableEntity
{
    public required string TestProperty { get; set; }
}
