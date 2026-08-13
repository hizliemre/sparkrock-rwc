using domain.Security;
using features.tests.Fakes;
using infra.persistence.postgre;
using infra.persistence.postgre.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace features.tests;

/// <summary>
///     Builds the real <see cref="SparkrockRwcDbContext" /> on the in-memory provider, so tests
///     exercise the production model configuration — including the reflective soft-delete filter and
///     the audit interceptor — rather than a stand-in.
/// </summary>
/// <remarks>
///     The interceptor is registered here deliberately. It was previously wired only in the Postgres
///     registration, so audit stamping and the delete rewrite were exercised by nothing.
///     <para>
///         The identity defaults to a <b>non-admin with no schools</b>. The production stub is a
///         system administrator, and a test double that copied it would let a handler omit its
///         authorisation scoping entirely without a single test failing.
///     </para>
/// </remarks>
internal static class InMemoryDbContextFactory
{
    internal static readonly DateTimeOffset DefaultNow = new(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);

    public static SparkrockRwcDbContext Create(
        TimeProvider? clock = null,
        ICurrentUser? currentUser = null,
        IAuditOverride? auditOverride = null)
    {
        AuditableEntityInterceptor interceptor = new(
            currentUser ?? new FakeCurrentUser(),
            clock ?? new FakeTimeProvider(DefaultNow),
            auditOverride ?? new AuditOverride());

        DbContextOptions<SparkrockRwcDbContext> options = new DbContextOptionsBuilder<SparkrockRwcDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(interceptor)
            .Options;

        return new SparkrockRwcDbContext(options);
    }

    /// <summary>A clock a test can advance between saves to order rows deterministically.</summary>
    public static FakeTimeProvider Clock() => new(DefaultNow);
}
