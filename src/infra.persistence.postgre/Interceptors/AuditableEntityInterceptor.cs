using domain.Abstraction;
using domain.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace infra.persistence.postgre.Interceptors;

/// <summary>
///     Stamps audit attribution, rewrites deletes on soft-deletable entities, and refuses them
///     everywhere else.
/// </summary>
/// <remarks>
///     Registered <b>scoped</b>. It consumes a scoped <see cref="ICurrentUser" />, and holding one in
///     a singleton would capture the first request's identity for the process lifetime.
///     <para>
///         The delete refusal is the load-bearing half of the base-class split. Splitting makes
///         <em>soft</em> deletion inexpressible for reference entities, not deletion:
///         <c>Remove(school)</c> still compiles, and with no rewrite to catch it EF issues a real
///         <c>DELETE</c> that cascades to the school's students. The rule is total rather than
///         category-based — no marker interface, nothing to forget when an entity is added.
///     </para>
/// </remarks>
internal sealed class AuditableEntityInterceptor(
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    IAuditOverride auditOverride) : SaveChangesInterceptor
{
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

    private void ApplyAuditInformation(DbContext? context)
    {
        if (context is null)
            return;

        DateTimeOffset now = timeProvider.GetUtcNow();
        Guid actor = auditOverride.IsActive ? auditOverride.ActingUserId : currentUser.UserId;

        foreach (EntityEntry<BaseEntity> entry in context.ChangeTracker.Entries<BaseEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    ApplyCreated(entry.Entity, now, actor);
                    break;

                case EntityState.Modified:
                    IAuditableEntity modified = entry.Entity;
                    modified.ModifiedAt = now;
                    modified.ModifiedBy = actor;
                    break;

                case EntityState.Deleted:
                    ApplyDelete(entry, now, actor);
                    break;
            }
        }
    }

    private void ApplyCreated(BaseEntity entity, DateTimeOffset now, Guid actor)
    {
        IAuditableEntity audited = entity;
        audited.CreatedBy = actor;

        // Under an import override, an already-populated CreatedAt is a legacy instant and is the
        // only genuine audit data the old system had. Overwriting it would make every imported row
        // claim to have been created at import time.
        bool preserveLegacyInstant = auditOverride.IsActive && audited.CreatedAt != default;

        if (!preserveLegacyInstant)
            audited.CreatedAt = now;
    }

    private static void ApplyDelete(EntityEntry<BaseEntity> entry, DateTimeOffset now, Guid actor)
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
        deleted.DeletedBy = actor;

        IAuditableEntity audited = softDeletable;
        audited.ModifiedAt = now;
        audited.ModifiedBy = actor;
    }
}
