using Carter;
using features.Attendance;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace features.tests.Attendance;

/// <summary>
///     Spec §9 acceptance criterion 1: where the route mounts, what it is called, and one
///     <c>.ProducesProblem</c> per row of spec §7 — exactly, not a superset.
/// </summary>
/// <remarks>
///     The path assertion is the load-bearing one. F08 is <b>student-scoped, not school-nested</b>
///     (conventions §1, spec §4.2): its rows span schools by design, so nesting it under
///     <c>/schools/{schoolId}</c> would imply a tenant scope the query deliberately does not apply.
/// </remarks>
public sealed class GetStudentAttendanceHistoryEndpointTests
{
    private const string Path = "api/v1/students/{studentId}/attendance";

    [Fact]
    public void Endpoint_MountsUnderStudentsAndNotUnderSchools()
    {
        RouteEndpoint endpoint = Single();

        Assert.Equal(Path, endpoint.RoutePattern.RawText);
        Assert.DoesNotContain("schools", endpoint.RoutePattern.RawText!, StringComparison.Ordinal);
    }

    [Fact]
    public void Endpoint_IsNamedAndTagged()
    {
        RouteEndpoint endpoint = Single();

        Assert.Equal(
            nameof(GetStudentAttendanceHistory),
            endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName);

        Assert.Contains("Attendance", endpoint.Metadata.GetMetadata<ITagsMetadata>()?.Tags ?? []);
    }

    [Fact]
    public void Endpoint_DeclaresExactlyTheDocumentedStatuses()
    {
        int[] declared = Single().Metadata
            .OfType<IProducesResponseTypeMetadata>()
            .Select(metadata => metadata.StatusCode)
            .Order()
            .ToArray();

        Assert.Equal([200, 400, 404], declared);
    }

    /// <summary>A guard on the guards: every assertion above is vacuous if nothing was discovered.</summary>
    [Fact]
    public void Endpoint_IsMappedExactlyOnce() =>
        Assert.Single(
            MappedRoutes(),
            route => string.Equals(route.RoutePattern.RawText, Path, StringComparison.Ordinal));

    private static RouteEndpoint Single() =>
        Assert.Single(
            MappedRoutes(),
            route => string.Equals(route.RoutePattern.RawText, Path, StringComparison.Ordinal)
                     && route.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains("GET"));

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
