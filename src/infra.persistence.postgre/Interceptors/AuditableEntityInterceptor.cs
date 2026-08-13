using domain.Abstraction;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace infra.persistence.postgre.Interceptors;

/// <summary>
///     Stamps audit attribution, rewrites deletes on soft-deletable entities, and refuses them
///     everywhere else.
/// </summary>
/// <remarks>
///     The refusal is DEC-20's load-bearing half. Splitting the base class makes <em>soft</em>
///     deletion inexpressible for reference entities, not deletion: <c>Remove(school)</c> still
///     compiles, and with no rewrite to catch it EF issues a real <c>DELETE</c> that cascades to the
///     school's students. The rule here is total rather than category-based — no marker interface and
///     no per-type list, so there is nothing to forget when an entity is added.
/// </remarks>
internal sealed class AuditableEntityInterceptor : SaveChangesInterceptor
{
    // The project has no authorization yet, so every write is attributed to this placeholder user.
    // Replaced by ICurrentUser in T01a-07.
    private static readonly Guid SystemUserId = Guid.Empty;

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ApplyAuditInformation(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ApplyAuditInformation(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void ApplyAuditInformation(DbContext? context)
    {
        if (context is null)
            return;

        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (EntityEntry<BaseEntity> entry in context.ChangeTracker.Entries<BaseEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    IAuditableEntity added = entry.Entity;
                    added.CreatedAt = now;
                    added.CreatedBy = SystemUserId;
                    break;

                case EntityState.Modified:
                    IAuditableEntity modified = entry.Entity;
                    modified.ModifiedAt = now;
                    modified.ModifiedBy = SystemUserId;
                    break;

                case EntityState.Deleted:
                    ApplyDelete(entry, now);
                    break;
            }
        }
    }

    private static void ApplyDelete(EntityEntry<BaseEntity> entry, DateTimeOffset now)
    {
        if (entry.Entity is not SoftDeletableEntity softDeletable)
        {
            throw new InvalidOperationException(
                $"{entry.Entity.GetType().Name} is not soft-deletable and cannot be removed. Physical "
                + "deletion would bypass the query filter and cascade to dependents; deactivate the row "
                + "instead, or use the audited purge.");
        }

        // Turn the DELETE into an UPDATE - the global query filter hides the row afterwards.
        entry.State = EntityState.Modified;

        ISoftDeletable deleted = softDeletable;
        deleted.IsDeleted = true;
        deleted.DeletedAt = now;
        deleted.DeletedBy = SystemUserId;

        IAuditableEntity audited = softDeletable;
        audited.ModifiedAt = now;
        audited.ModifiedBy = SystemUserId;
    }
}
