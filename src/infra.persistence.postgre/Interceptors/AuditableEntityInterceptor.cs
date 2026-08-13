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
/// </remarks>
internal sealed class AuditableEntityInterceptor(
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    IAuditOverride auditOverride) : SaveChangesInterceptor
{
    private static readonly string[] SoftDeleteColumns =
    [
        nameof(ISoftDeletable.IsDeleted),
        nameof(ISoftDeletable.DeletedAt),
        nameof(ISoftDeletable.DeletedBy),
        nameof(IAuditableEntity.ModifiedAt),
        nameof(IAuditableEntity.ModifiedBy)
    ];

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

        EntityEntry<BaseEntity>[] entries = context.ChangeTracker.Entries<BaseEntity>().ToArray();

        // Validate before mutating anything. Rewriting as we go means a refusal partway through
        // leaves earlier entries already stamped and already converted to soft deletes, so a caller
        // that catches the exception and saves again silently commits them.
        RejectUnsupportedDeletes(entries);

        DateTimeOffset now = timeProvider.GetUtcNow();
        Guid actor = auditOverride.IsActive ? auditOverride.ActingUserId : currentUser.UserId;

        foreach (EntityEntry<BaseEntity> entry in entries)
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
                    ApplySoftDelete(entry, now, actor);
                    break;
            }
        }
    }

    private static void RejectUnsupportedDeletes(IEnumerable<EntityEntry<BaseEntity>> entries)
    {
        foreach (EntityEntry<BaseEntity> entry in entries)
        {
            if (entry.State is not EntityState.Deleted || entry.Entity is SoftDeletableEntity)
                continue;

            throw new InvalidOperationException(
                $"{entry.Entity.GetType().Name} is not soft-deletable and cannot be removed. Physical "
                + "deletion would bypass the query filter; deactivate the row instead, or use the audited "
                + "purge. Note that a cascade resolves at Remove() time, so this may be a dependent of the "
                + "entity you removed rather than the entity itself.");
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

    /// <summary>
    ///     Converts a delete into an update of only the columns the rewrite owns.
    /// </summary>
    /// <remarks>
    ///     Setting <c>State = Modified</c> marks <em>every</em> property modified, which is silent
    ///     data destruction for the ordinary delete-by-id pattern: <c>Remove(new Student { Id = id })</c>
    ///     is a stub whose other properties are defaults, and EF writes all of them. Verified — the
    ///     row came back with an empty name, a zero <c>CreatedBy</c> and a year-0001 <c>CreatedAt</c>,
    ///     with no error, because those are all legal values.
    ///     <para>
    ///         Starting from <c>Unchanged</c> and marking five properties keeps the rest of the row
    ///         untouched whether the entity was loaded or stubbed.
    ///     </para>
    /// </remarks>
    private static void ApplySoftDelete(EntityEntry<BaseEntity> entry, DateTimeOffset now, Guid actor)
    {
        SoftDeletableEntity softDeletable = (SoftDeletableEntity)entry.Entity;

        entry.State = EntityState.Unchanged;

        ISoftDeletable deleted = softDeletable;
        deleted.IsDeleted = true;
        deleted.DeletedAt = now;
        deleted.DeletedBy = actor;

        IAuditableEntity audited = softDeletable;
        audited.ModifiedAt = now;
        audited.ModifiedBy = actor;

        foreach (string column in SoftDeleteColumns)
            entry.Property(column).IsModified = true;
    }
}
