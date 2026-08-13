namespace domain.Abstraction;

/// <summary>
///     A <see cref="BaseEntity" /> that is hidden rather than removed, and filtered out of every
///     query by the reflective filter in the DbContext.
/// </summary>
/// <remarks>
///     Deriving from this type is what opts an entity into soft delete. Only transactional records do
///     — reference data uses an <c>IsActive</c> flag instead, so that deactivating a school cannot
///     make its students vanish from every projection through it.
/// </remarks>
public abstract class SoftDeletableEntity : BaseEntity, ISoftDeletable
{
    public bool IsDeleted { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public Guid? DeletedBy { get; private set; }

    bool ISoftDeletable.IsDeleted { get => IsDeleted; set => IsDeleted = value; }

    DateTimeOffset? ISoftDeletable.DeletedAt { get => DeletedAt; set => DeletedAt = value; }

    Guid? ISoftDeletable.DeletedBy { get => DeletedBy; set => DeletedBy = value; }
}
