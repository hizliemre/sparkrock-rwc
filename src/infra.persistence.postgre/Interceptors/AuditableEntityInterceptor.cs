using domain.Abstraction;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace infra.persistence.postgre.Interceptors;

internal sealed class AuditableEntityInterceptor : SaveChangesInterceptor
{
    // The project has no authorization yet, so every write is attributed to this placeholder user.
    // Replace with the authenticated user id once auth is in place.
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
                    entry.Entity.CreatedAt = now;
                    entry.Entity.CreatedBy = SystemUserId;
                    break;

                case EntityState.Modified:
                    entry.Entity.ModifiedAt = now;
                    entry.Entity.ModifiedBy = SystemUserId;
                    break;

                case EntityState.Deleted:
                    // Turn the DELETE into an UPDATE - the global query filter hides the row afterwards.
                    entry.State = EntityState.Modified;
                    entry.Entity.IsDeleted = true;
                    entry.Entity.DeletedAt = now;
                    entry.Entity.DeletedBy = SystemUserId;
                    entry.Entity.ModifiedAt = now;
                    entry.Entity.ModifiedBy = SystemUserId;
                    break;
            }
        }
    }
}
