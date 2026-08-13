namespace domain.Abstraction;

/// <summary>
///     Deletion attribution for entities that are hidden rather than removed.
/// </summary>
/// <remarks>
///     Separated from <see cref="IAuditableEntity" /> by DEC-20 so the two lifecycles are distinct in
///     the type system: only <see cref="SoftDeletableEntity" /> carries these columns, and only those
///     entities get the reflective query filter.
/// </remarks>
public interface ISoftDeletable
{
    /// <summary>Whether the entity has been marked deleted.</summary>
    bool IsDeleted { get; internal set; }

    /// <summary>The user who deleted this entity.</summary>
    Guid? DeletedBy { get; internal set; }

    /// <summary>When this entity was deleted, or null if it has not been.</summary>
    DateTimeOffset? DeletedAt { get; internal set; }
}
