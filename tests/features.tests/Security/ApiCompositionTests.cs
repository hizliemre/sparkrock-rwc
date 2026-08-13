using api;
using domain.Security;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using service.defaults;

namespace features.tests.Security;

/// <summary>
///     What <c>WithApi</c> actually puts in the container.
/// </summary>
/// <remarks>
///     The HTTP edge is where the anonymous build's remaining exposure lives, so its registrations are
///     asserted rather than read. Every test here drives the real extension method — a test that
///     rebuilt the registrations itself would pass while the composition root said something else.
/// </remarks>
public sealed class ApiCompositionTests
{
    private static ServiceProvider Provider(params (string Key, string Value)[] configuration)
    {
        HostApplicationBuilder hostBuilder = Host.CreateEmptyApplicationBuilder(
            new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });

        Dictionary<string, string?> values = new()
        {
            [DeploymentGuard.AllowAnonymousStubIdentityKey] = "true",
            ["ConnectionStrings:sparkrock-rwc"] = "Host=localhost;Database=x"
        };

        foreach ((string key, string value) in configuration)
            values[key] = value;

        hostBuilder.Configuration.AddInMemoryCollection(values);
        hostBuilder.AddSparkrockRwc().WithApi();

        return hostBuilder.Services.BuildServiceProvider();
    }

    private static CorsPolicy Policy(params (string Key, string Value)[] configuration)
    {
        using ServiceProvider provider = Provider(configuration);

        CorsPolicy? policy = provider
            .GetRequiredService<IOptions<CorsOptions>>()
            .Value
            // Qualified: the test's own namespace chain reaches features.ServiceExtensions first.
            .GetPolicy(api.ServiceExtensions.CorsPolicyName);

        Assert.NotNull(policy);

        return policy;
    }

    /// <summary>
    ///     The one combination that is always unsafe: any origin, reflected, with credentials. The
    ///     browser then treats every page the developer visits as entitled to read every student
    ///     record, and with no authentication there is nothing for the same-origin policy to fall back
    ///     on. Scalar is served same-origin at <c>/scalar/v1</c> and never needed it.
    /// </summary>
    [Fact]
    public void WithApi_NeverCombinesCredentialsWithAWildcardOrigin()
    {
        CorsPolicy policy = Policy(("Cors:AllowedOrigins:0", "https://example.test"));

        Assert.False(policy.AllowAnyOrigin);
        Assert.False(policy.SupportsCredentials);
    }

    [Fact]
    public void WithApi_WhenNoOriginsAreConfigured_AllowsNone()
    {
        CorsPolicy policy = Policy();

        Assert.Empty(policy.Origins);
        Assert.False(policy.AllowAnyOrigin);
        Assert.False(policy.SupportsCredentials);
    }

    [Fact]
    public void WithApi_WhenOriginsAreConfigured_AllowsExactlyThose()
    {
        CorsPolicy policy = Policy(
            ("Cors:AllowedOrigins:0", "https://one.test"),
            ("Cors:AllowedOrigins:1", "https://two.test"));

        Assert.Equal(["https://one.test", "https://two.test"], policy.Origins);
        Assert.False(policy.IsOriginAllowed("https://three.test"));
        Assert.True(policy.IsOriginAllowed("https://one.test"));
    }

    /// <summary>
    ///     <c>IAuditOverride</c> suppresses <c>CreatedAt</c> stamping and attributes writes to an
    ///     arbitrary actor. <c>Begin(Guid)</c> is public on a public interface and the request pipeline
    ///     resolves it per request, so registering the real implementation makes audit attribution
    ///     forgeable by any handler that asks for it. Nothing calls it — the importer does not exist —
    ///     so the API registers a null object and the real one stays reachable only from the importer's
    ///     own composition root.
    /// </summary>
    [Fact]
    public void WithApi_RegistersAnAuditOverrideThatCannotBeActivated()
    {
        using ServiceProvider provider = Provider();
        using IServiceScope scope = provider.CreateScope();

        IAuditOverride auditOverride = scope.ServiceProvider.GetRequiredService<IAuditOverride>();

        Assert.False(auditOverride.IsActive);
        Assert.Throws<InvalidOperationException>(() => auditOverride.Begin(Guid.NewGuid()));
        Assert.False(auditOverride.IsActive);
    }

    [Fact]
    public void WithApi_DoesNotRegisterTheForgeableAuditOverride()
    {
        using ServiceProvider provider = Provider();
        using IServiceScope scope = provider.CreateScope();

        Assert.IsNotType<AuditOverride>(scope.ServiceProvider.GetRequiredService<IAuditOverride>());
    }

    [Fact]
    public void WithApi_RegistersTheStubIdentity()
    {
        using ServiceProvider provider = Provider();
        using IServiceScope scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ICurrentUser>());
    }

    /// <summary>
    ///     The guard runs before anything anonymous is registered, so a build that should not start
    ///     does not get as far as having a container.
    /// </summary>
    [Fact]
    public void WithApi_WhenTheOptInIsAbsent_Throws()
    {
        HostApplicationBuilder hostBuilder = Host.CreateEmptyApplicationBuilder(
            new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });

        Assert.Throws<InvalidOperationException>(() => hostBuilder.AddSparkrockRwc().WithApi());
    }
}
