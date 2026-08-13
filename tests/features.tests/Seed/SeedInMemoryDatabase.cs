using domain.Security;
using features.tests.Fakes;
using infra.persistence.postgre;
using infra.persistence.postgre.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace features.tests.Seed;

/// <summary>
///     One in-memory database that survives across several <see cref="SparkrockRwcDbContext" />
///     instances.
/// </summary>
/// <remarks>
///     <c>InMemoryDbContextFactory.Create</c> builds a <em>fresh</em> database per call, which is the
///     right default everywhere else and is wrong for exactly one thing: idempotency. "Runs twice
///     without producing a second copy" has to mean the second run sees the first run's rows, through
///     a context whose change tracker is empty — otherwise <c>FindAsync</c> answers from memory and
///     the test proves that a tracked entity is still tracked.
///     <para>
///         Otherwise identical to <c>InMemoryDbContextFactory</c>: the real model, the real
///         <see cref="AuditableEntityInterceptor" />, and an identity that defaults to a non-admin
///         with no schools.
///     </para>
/// </remarks>
internal sealed class SeedInMemoryDatabase
{
    private readonly string _name = Guid.NewGuid().ToString();

    public FakeTimeProvider Clock { get; } = new(InMemoryDbContextFactory.DefaultNow);

    public SparkrockRwcDbContext Connect(
        ICurrentUser? currentUser = null,
        IAuditOverride? auditOverride = null)
    {
        AuditableEntityInterceptor interceptor = new(
            currentUser ?? new FakeCurrentUser(),
            Clock,
            auditOverride ?? new AuditOverride());

        DbContextOptions<SparkrockRwcDbContext> options = new DbContextOptionsBuilder<SparkrockRwcDbContext>()
            .UseInMemoryDatabase(_name)
            .AddInterceptors(interceptor)
            .Options;

        return new SparkrockRwcDbContext(options);
    }
}
