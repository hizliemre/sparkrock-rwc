namespace domain.Abstraction;

public interface IAuditableEntity
{
    /// <summary>
    ///     The user who created this entity.
    /// </summary>
    Guid CreatedBy { get; internal set; }
    /// <summary>
    ///     The date and time when this entity was created.
    /// </summary>
    DateTimeOffset CreatedAt { get; internal set; }
    /// <summary>
    ///     The user who last modified this entity.
    /// </summary>
    Guid? ModifiedBy { get; internal set; }
    /// <summary>
    ///     The date and time when this entity was last modified.
    /// </summary>
    DateTimeOffset? ModifiedAt { get; internal set; }
    /// <summary>
    ///     Indicates whether the entity has been marked as deleted.
    /// </summary>
    bool IsDeleted { get; internal set; }
    /// <summary>
    ///     The identifier of the user who deleted this entity.
    /// </summary>
    Guid? DeletedBy { get; internal set; }
    /// <summary>
    ///     The date and time when this entity was deleted, or null if the entity has not been deleted.
    /// </summary>
    DateTimeOffset? DeletedAt { get; internal set; }
}