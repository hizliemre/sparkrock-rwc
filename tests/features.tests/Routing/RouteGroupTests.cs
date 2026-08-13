using System.Reflection;
using Carter;
using features.TestEntities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace features.tests.Routing;

/// <summary>
///     Where <c>UseSparkrockRwc</c> mounts the Carter modules.
/// </summary>
/// <remarks>
///     The version segment lives on the group, not on each route, so a module writing
///     <c>/test-entities</c> is served at <c>/api/v1/test-entities</c> and a module writing
///     <c>/api/...</c> would double the prefix. Twelve workstreams will each add modules against that
///     assumption, and the only place it is stated is one <c>MapGroup</c> call.
///     <para>
///         <b>What this cannot cover.</b> Production discovery goes through
///         <c>DependencyContextAssemblyCatalog(Assembly.GetEntryAssembly())</c>, and the entry
///         assembly under the test runner is the test host — which references neither Carter nor
///         FluentValidation, so the catalog finds nothing and the walk yields zero endpoints. The
///         assembly is therefore supplied explicitly here. That makes these assertions about the
///         group's prefix and about the modules that exist, <em>not</em> about the production
///         catalog resolving the right assembly: a regression that made <c>GetEntryAssembly()</c>
///         return the wrong thing would leave every test here green and every route unmapped. Closing
///         that needs a <c>WebApplicationFactory</c>-hosted assembly, which is the integration tier.
///     </para>
/// </remarks>
public sealed class RouteGroupTests
{
    private const string ExpectedPrefix = "api/v1/";

    private static RouteEndpoint[] MappedRoutes()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        // Endpoint construction, not invocation, needs IMediator to be a *known* service:
        // RequestDelegateFactory refuses to infer a handler parameter it cannot classify. Nothing
        // below sends a request, so no handler dependency has to resolve.
        builder.Services.AddMediatR(configuration =>
            configuration.RegisterServicesFromAssembly(typeof(ServiceExtensions).Assembly));

        // The params overload, not the single-Assembly one: that one treats its argument as the entry
        // assembly and reads the dependency context from it.
        builder.Services.AddCarter(new DependencyContextAssemblyCatalog([typeof(ServiceExtensions).Assembly]));

        WebApplication app = builder.Build();
        app.UseSparkrockRwc();

        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
    }

    /// <summary>
    ///     A guard on the guards: every assertion below is vacuous if nothing was discovered.
    /// </summary>
    /// <remarks>
    ///     Non-empty rather than an exact count. Twelve workstreams add modules to this assembly, so an
    ///     exact count is a merge conflict that reads as a routing regression — and it would fail on
    ///     the one thing this file has no opinion about, which is how many endpoints exist.
    /// </remarks>
    [Fact]
    public void UseSparkrockRwc_MapsTheModulesThatExist()
    {
        Assert.NotEmpty(MappedRoutes());
    }

    [Fact]
    public void UseSparkrockRwc_MountsEveryRouteUnderTheVersionedGroup()
    {
        Assert.All(
            MappedRoutes(),
            route => Assert.StartsWith(ExpectedPrefix, route.RoutePattern.RawText, StringComparison.Ordinal));
    }

    /// <summary>
    ///     A module writing <c>/api/...</c> would be served at <c>/api/v1/api/...</c>. The doubled
    ///     prefix is the specific mistake the group exists to make unnecessary.
    /// </summary>
    [Fact]
    public void UseSparkrockRwc_DoesNotDoubleThePrefix()
    {
        Assert.All(MappedRoutes(), route => Assert.DoesNotContain(
            "api/v1/api", route.RoutePattern.RawText ?? string.Empty, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("api/v1/test-entities")]
    public void UseSparkrockRwc_MapsTheReferenceSliceAtItsDocumentedPath(string expected)
    {
        Assert.Contains(
            MappedRoutes(),
            route => string.Equals(route.RoutePattern.RawText, expected, StringComparison.Ordinal));
    }

    /// <summary>
    ///     Conventions §1 names the group <c>api/v1</c>. Asserting the literal keeps the version from
    ///     drifting into a route or out of the group entirely.
    /// </summary>
    [Fact]
    public void UseSparkrockRwc_UsesTheVersionedGroupNotTheBareApiGroup()
    {
        Assert.All(MappedRoutes(), route => Assert.NotEqual(
            "api/test-entities", route.RoutePattern.RawText));
    }

    /// <summary>
    ///     Every module discovered is one of ours, so the walk above is measuring what it claims to.
    /// </summary>
    [Fact]
    public void UseSparkrockRwc_DiscoversOnlyModulesFromTheFeaturesAssembly()
    {
        Assembly featuresAssembly = typeof(ServiceExtensions).Assembly;

        Assert.All(MappedRoutes(), route => Assert.NotNull(route.DisplayName));
        Assert.Contains(
            featuresAssembly.GetTypes(),
            type => typeof(ICarterModule).IsAssignableFrom(type) && type == typeof(GetTestEntities.Endpoint));
    }

    /// <summary>
    ///     The route table is a contract; a route with no name cannot be referenced by
    ///     <c>Location</c> generation or by the OpenAPI document.
    /// </summary>
    [Fact]
    public void UseSparkrockRwc_GivesEveryRouteAName()
    {
        Assert.All(MappedRoutes(), route => Assert.NotNull(route.Metadata.GetMetadata<IEndpointNameMetadata>()));
    }
}
