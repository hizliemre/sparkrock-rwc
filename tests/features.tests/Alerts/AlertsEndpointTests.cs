using Carter;
using features;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace features.tests.Alerts;

/// <summary>
///     Acceptance criteria 1 and 2: two routes, their names, their tag, and one
///     <c>.ProducesProblem</c> per row of spec §7 — <b>exactly</b>, not a superset.
/// </summary>
/// <remarks>
///     A superset is not harmless. <c>.ProducesProblem</c> is the OpenAPI document a client generates
///     from, so a documented status a route cannot raise produces a branch in generated code that is
///     dead by construction.
///     <para>
///         The resolution route is asserted three ways because conventions §1's F10 row is the thing
///         being amended (O-02): the path is <c>resolution</c> singular, the verb is <c>POST</c>, and
///         <b>201 is not declared</b> — there is nothing created for a <c>Location</c> to point at.
///     </para>
/// </remarks>
public sealed class AlertsEndpointTests
{
    public static TheoryData<string, string, string, int[]> Routes() => new()
    {
        { "api/v1/schools/{schoolId:guid}/alerts", "GET", "GetSchoolAlerts", [400, 404] },
        { "api/v1/alerts/{alertId:guid}/resolution", "POST", "ResolveAlert", [400, 404, 409] }
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public void Endpoint_DeclaresExactlyTheDocumentedStatuses(
        string path,
        string method,
        string name,
        int[] problemStatuses)
    {
        RouteEndpoint endpoint = Single(path);

        Assert.Equal(name, endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName);
        Assert.Contains("Alerts", endpoint.Metadata.GetMetadata<ITagsMetadata>()?.Tags ?? []);
        Assert.Contains(method, endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);

        int[] declared = endpoint.Metadata
            .OfType<IProducesResponseTypeMetadata>()
            .Select(metadata => metadata.StatusCode)
            .Order()
            .ToArray();

        Assert.Equal([StatusCodes.Status200OK, .. problemStatuses.Order()], declared);
    }

    /// <summary>
    ///     O-02: the resource is the singular resolution state DEC-18 models, and it answers 200.
    ///     A 201 would need a <c>Location</c>, which would have to point at the alert — which is not
    ///     what was created, because nothing was created.
    /// </summary>
    [Fact]
    public void ResolutionRoute_IsSingularAndDoesNotAnnounce201()
    {
        RouteEndpoint endpoint = Single("api/v1/alerts/{alertId:guid}/resolution");

        Assert.DoesNotContain(
            StatusCodes.Status201Created,
            endpoint.Metadata.OfType<IProducesResponseTypeMetadata>().Select(metadata => metadata.StatusCode));

        Assert.DoesNotContain(
            AlertRoutes(),
            route => (route.RoutePattern.RawText ?? string.Empty)
                .EndsWith("/resolutions", StringComparison.Ordinal));
    }

    /// <summary>A guard on the guard: two routes, each mapped once.</summary>
    [Fact]
    public void Endpoints_AreTwoAndNoMore() => Assert.Equal(2, AlertRoutes().Length);

    private static RouteEndpoint[] AlertRoutes() =>
        MappedRoutes()
            .Where(route => (route.RoutePattern.RawText ?? string.Empty)
                .Contains("alert", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static RouteEndpoint Single(string path) =>
        Assert.Single(
            MappedRoutes(),
            route => string.Equals(route.RoutePattern.RawText, path, StringComparison.Ordinal));

    private static RouteEndpoint[] MappedRoutes()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        builder.Services.AddMediatR(configuration =>
            configuration.RegisterServicesFromAssembly(typeof(ServiceExtensions).Assembly));
        builder.Services.AddCarter(new DependencyContextAssemblyCatalog([typeof(ServiceExtensions).Assembly]));

        WebApplication app = builder.Build();
        app.UseSparkrockRwc();

        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
    }
}
