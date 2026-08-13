using domain;
using Microsoft.EntityFrameworkCore;

namespace infra.persistence.sql;

public interface IDbContext
{
    DbSet<TestEntity> TestEntities { get; set; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}