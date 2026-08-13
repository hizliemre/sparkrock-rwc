using Carter;
using features;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace features.tests.Absenteeism;

/// <summary>
///     Acceptance criterion 1 and T09-11: two routes, their names, their tag, and one
///     <c>.ProducesProblem</c> per row of spec §9 — <b>exactly</b>, not a superset.
/// </summary>
/// <remarks>
///     A superset is not harmless. <c>.ProducesProblem</c> is the OpenAPI document a client generates
///     from, so a documented status a route cannot raise produces a branch in generated code that is
///     dead by construction.
///     <para>
///         The single-student route is asserted <b>not</b> to be nested under <c>schools</c>:
///         conventions §1 makes F09-single student-scoped because the figure spans schools within
///         the school year (V-07c).
///     </para>
/// </remarks>
public sealed class AbsenteeismEndpointTests
{
    public static TheoryData<string, string, int[]> Routes() => new()
    {
        { "api/v1/students/{studentId:guid}/absenteeism", "GetStudentAbsenteeism", [400, 404] },
        { "api/v1/schools/{schoolId:guid}/absenteeism", "GetSchoolAbsenteeism", [400, 404] }
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public void Endpoint_DeclaresExactlyTheDocumentedStatuses(string path, string name, int[] problemStatuses)
    {
        RouteEndpoint endpoint = Single(path);

        Assert.Equal(name, endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName);
        Assert.Contains("Absenteeism", endpoint.Metadata.GetMetadata<ITagsMetadata>()?.Tags ?? []);
        Assert.Contains("GET", endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);

        int[] declared = endpoint.Metadata
            .OfType<IProducesResponseTypeMetadata>()
            .Select(metadata => metadata.StatusCode)
            .Order()
            .ToArray();

        Assert.Equal([StatusCodes.Status200OK, .. problemStatuses.Order()], declared);
    }

    /// <summary>
    ///     Conventions §1: F09's single-student route is student-scoped, never nested under a school.
    /// </summary>
    [Fact]
    public void Endpoint_SingleStudentRouteIsNotNestedUnderSchools()
    {
        Assert.DoesNotContain(
            AbsenteeismRoutes(),
            route => (route.RoutePattern.RawText ?? string.Empty)
                .StartsWith("api/v1/schools/{schoolId:guid}/students", StringComparison.Ordinal));
    }

    /// <summary>A guard on the guard: two routes, each mapped once.</summary>
    [Fact]
    public void Endpoints_AreTwoAndNoMore() => Assert.Equal(2, AbsenteeismRoutes().Length);

    private static RouteEndpoint[] AbsenteeismRoutes() =>
        MappedRoutes()
            .Where(route => (route.RoutePattern.RawText ?? string.Empty)
                .EndsWith("/absenteeism", StringComparison.Ordinal))
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
