using domain.Security;
using features.integration.tests.Fakes;
using infra.persistence.postgre;
using infra.persistence.postgre.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace features.integration.tests;

/// <summary>
///     Builds the real <see cref="SparkrockRwcDbContext" /> against the Testcontainers PostgreSQL
///     instance, with the same interceptor wiring the in-memory factory uses.
/// </summary>
/// <remarks>
///     The twin of <c>tests/features.tests/InMemoryDbContextFactory.cs</c> — same parameters, same
///     defaults, same interceptor construction. Any difference between the two should be a provider
///     difference and nothing else; a gratuitous one reads as a meaningful one.
///     <para>
///         Two things are load-bearing and easy to drop. <see cref="AuditableEntityInterceptor" /> is
///         registered in <c>WithPostgre</c> rather than in the context, so a factory that omits it
///         exercises neither audit stamping nor the DEC-20 delete rewrite. And
///         <c>UseSnakeCaseNamingConvention()</c> must match both <c>WithPostgre</c> and the
///         design-time <c>DbContextFactory</c>, or the migration creates snake_case tables that the
///         tests then query as PascalCase.
///     </para>
///     <para>
///         The identity defaults to a <b>non-admin with no schools</b>, for the reason
///         <see cref="FakeCurrentUser" /> documents.
///     </para>
/// </remarks>
internal static class ContainerDbContextFactory
{
    internal static readonly DateTimeOffset DefaultNow = new(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);

    public static SparkrockRwcDbContext Create(
        string connectionString,
        TimeProvider? clock = null,
        ICurrentUser? currentUser = null,
        IAuditOverride? auditOverride = null)
    {
        AuditableEntityInterceptor interceptor = new(
            currentUser ?? new FakeCurrentUser(),
            clock ?? new FakeTimeProvider(DefaultNow),
            auditOverride ?? new AuditOverride());

        DbContextOptions<SparkrockRwcDbContext> options = new DbContextOptionsBuilder<SparkrockRwcDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(interceptor)
            .Options;

        return new SparkrockRwcDbContext(options);
    }

    /// <summary>A clock a test can advance between saves to order rows deterministically.</summary>
    public static FakeTimeProvider Clock() => new(DefaultNow);
}
