using domain;
using Microsoft.EntityFrameworkCore;

namespace infra.persistence.sql;

public interface IDbContext
{
    public DbSet<TestEntity> TestEntities { get; set; }
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}