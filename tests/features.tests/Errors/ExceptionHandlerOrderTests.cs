using api;
using api.Errors;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using service.defaults;

namespace features.tests.Errors;

/// <summary>
///     Registration order is dispatch order.
/// </summary>
/// <remarks>
///     <c>ExceptionHandlerMiddleware</c> walks the registered <see cref="IExceptionHandler" />
///     instances in registration order and stops at the first that reports handled. The order is
///     therefore a behavioural contract, not a style choice: it is invisible today only because
///     <c>ValidationException</c> and the domain exceptions are disjoint types, and it stops being
///     invisible the moment a handler widens what it recognises. Asserting on the descriptors rather
///     than on a response keeps the check independent of which exceptions happen to exist.
/// </remarks>
public sealed class ExceptionHandlerOrderTests
{
    private static ServiceCollection ConfiguredServices()
    {
        HostApplicationBuilder hostBuilder = Host.CreateEmptyApplicationBuilder(
            new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });

        hostBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DeploymentGuard.AllowAnonymousStubIdentityKey] = "true",
            ["ConnectionStrings:sparkrock-rwc"] = "Host=localhost;Database=x"
        });

        hostBuilder.AddSparkrockRwc().WithApi();

        return (ServiceCollection)hostBuilder.Services;
    }

    private static Type[] RegisteredHandlers() =>
        ConfiguredServices()
            .Where(descriptor => descriptor.ServiceType == typeof(IExceptionHandler))
            .Select(descriptor => descriptor.ImplementationType!)
            .ToArray();

    /// <summary>
    ///     Most specific first. <c>ValidationExceptionHandler</c> recognises exactly one type and must
    ///     get the chance to claim it before anything broader.
    /// </summary>
    [Fact]
    public void WithApi_RegistersTheExceptionHandlersInDispatchOrder()
    {
        Assert.Equal(
            [typeof(ValidationExceptionHandler), typeof(DomainExceptionHandler)],
            RegisteredHandlers());
    }

    /// <summary>
    ///     A duplicate registration would run the same handler twice and, worse, silently change the
    ///     position of everything after it.
    /// </summary>
    [Fact]
    public void WithApi_RegistersEachExceptionHandlerExactlyOnce()
    {
        Type[] handlers = RegisteredHandlers();

        Assert.Equal(handlers.Length, handlers.Distinct().Count());
    }

    /// <summary>
    ///     Handlers are stateless and resolved once per pipeline; a scoped registration would be a
    ///     lifetime error at the root provider.
    /// </summary>
    [Fact]
    public void WithApi_RegistersTheExceptionHandlersAsSingletons()
    {
        Assert.All(
            ConfiguredServices().Where(descriptor => descriptor.ServiceType == typeof(IExceptionHandler)),
            descriptor => Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime));
    }
}
