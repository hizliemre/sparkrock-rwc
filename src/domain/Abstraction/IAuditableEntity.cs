namespace domain.Abstraction;

/// <summary>
///     Created and modified attribution, written only by the audit interceptor.
/// </summary>
/// <remarks>
///     The setters are internal so the interceptor can stamp through them while application code
///     cannot. <see cref="BaseEntity" /> implements them explicitly, which is what makes the
///     restriction real rather than advisory.
/// </remarks>
public interface IAuditableEntity
{
    /// <summary>The user who created this entity.</summary>
    Guid CreatedBy { get; internal set; }

    /// <summary>When this entity was created.</summary>
    DateTimeOffset CreatedAt { get; internal set; }

    /// <summary>The user who last modified this entity.</summary>
    Guid? ModifiedBy { get; internal set; }

    /// <summary>When this entity was last modified, or null if never.</summary>
    DateTimeOffset? ModifiedAt { get; internal set; }
}
