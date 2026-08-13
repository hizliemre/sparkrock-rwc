namespace domain.Abstraction;

/// <summary>
///     Identity and created/modified attribution. Carries no soft-delete state.
/// </summary>
/// <remarks>
///     DEC-20 split the delete columns out to <see cref="SoftDeletableEntity" />. An entity deriving
///     from this class gets no soft-delete columns, no query filter, and therefore no filtered
///     subquery joined into every projection that reaches it.
///     <para>
///         Soft-deleting one is not expressible; deleting one still is. The audit interceptor rejects
///         <c>EntityState.Deleted</c> for anything that is not a <see cref="SoftDeletableEntity" />,
///         which is the guard that stops a physical delete cascading to dependents.
///     </para>
/// </remarks>
public abstract class BaseEntity : IAuditableEntity
{
    /// <summary>Primary key. Public because the import assigns it; it is not an audit field.</summary>
    public Guid Id { get; set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset? ModifiedAt { get; private set; }

    public Guid? ModifiedBy { get; private set; }

    // Explicit implementations are what make the interface's internal setters mean something. The
    // public properties above are read-only to everyone; the interceptor stamps through these.
    DateTimeOffset IAuditableEntity.CreatedAt { get => CreatedAt; set => CreatedAt = value; }

    Guid IAuditableEntity.CreatedBy { get => CreatedBy; set => CreatedBy = value; }

    DateTimeOffset? IAuditableEntity.ModifiedAt { get => ModifiedAt; set => ModifiedAt = value; }

    Guid? IAuditableEntity.ModifiedBy { get => ModifiedBy; set => ModifiedBy = value; }
}
