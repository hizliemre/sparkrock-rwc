using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace features.tests.Model;

/// <summary>
///     Builds the real relational model without ever opening a connection.
/// </summary>
/// <remarks>
///     Model assertions need relational metadata — column types, index filters, check constraints —
///     which the in-memory provider does not produce. Npgsql builds all of it offline.
///     <para>
///         The naming convention here must match <c>WithPostgre</c> and <c>DbContextFactory</c>. If
///         it drifts, every assertion in this folder checks names the application never produces,
///         which is what <c>ModelHarnessTests</c> exists to catch.
///     </para>
/// </remarks>
internal static class ModelFactory
{
    public static IModel Create()
    {
        DbContextOptions<SparkrockRwcDbContext> options = new DbContextOptionsBuilder<SparkrockRwcDbContext>()
            .UseNpgsql("Host=model-only;Database=model-only")
            .UseSnakeCaseNamingConvention()
            .Options;

        using SparkrockRwcDbContext context = new(options);
        return context.Model;
    }
}
