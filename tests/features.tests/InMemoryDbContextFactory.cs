using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;

namespace features.tests;

/// <summary>
///     Builds the real <see cref="SparkrockRwcDbContext" /> on top of the in-memory provider, so tests exercise the
///     production model configuration - including the global soft-delete query filter - instead of a stand-in.
/// </summary>
internal static class InMemoryDbContextFactory
{
    public static SparkrockRwcDbContext Create()
    {
        DbContextOptions<SparkrockRwcDbContext> options = new DbContextOptionsBuilder<SparkrockRwcDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new SparkrockRwcDbContext(options);
    }
}
