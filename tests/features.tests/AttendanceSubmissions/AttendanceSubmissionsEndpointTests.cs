using Carter;
using features;
using features.Attendance;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace features.tests.AttendanceSubmissions;

/// <summary>
///     Acceptance criteria 1 and 2: two routes at the documented paths, with exactly the documented
///     statuses — and the detail route resolving the exact URL F07 emits in its <c>Location</c>.
/// </summary>
/// <remarks>
///     A superset of <c>.ProducesProblem</c> is not harmless: it is the OpenAPI document a client
///     generates from, so a documented status a route cannot raise produces a branch in generated code
///     that is dead by construction.
/// </remarks>
public sealed class AttendanceSubmissionsEndpointTests
{
    public static TheoryData<string, string, int[]> Routes() => new()
    {
        { "api/v1/schools/{schoolId:guid}/attendance-submissions", "GetSchoolAttendanceSubmissions", [400, 404] },
        { "api/v1/attendance-submissions/{submissionId:guid}", "GetAttendanceSubmissionById", [404] }
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public void Endpoint_DeclaresExactlyTheDocumentedStatuses(string path, string name, int[] problemStatuses)
    {
        RouteEndpoint endpoint = Single(path);

        Assert.Equal(name, endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName);
        Assert.Contains("AttendanceSubmissions", endpoint.Metadata.GetMetadata<ITagsMetadata>()?.Tags ?? []);
        Assert.Contains("GET", endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);

        int[] declared = endpoint.Metadata
            .OfType<IProducesResponseTypeMetadata>()
            .Select(metadata => metadata.StatusCode)
            .Order()
            .ToArray();

        Assert.Equal([StatusCodes.Status200OK, .. problemStatuses.Order()], declared);
    }

    /// <summary>
    ///     Acceptance criterion 2, mechanised: F07's <c>Location</c> is a hand-built string, so nothing
    ///     but this ties the two together.
    /// </summary>
    /// <remarks>
    ///     <b>Its limit, stated.</b> F07 builds its <c>Location</c> by interpolating a literal inline
    ///     in <c>SaveDailyAttendance.Endpoint</c>, and nothing exports it — so
    ///     <see cref="LocationEmittedBySaveDailyAttendance" /> is a transcription of that literal, not
    ///     a reference to it. This therefore catches <b>F11</b> moving its route out from under F07's
    ///     <c>Location</c>, which is the direction F11 can cause; it would not catch F07 changing its
    ///     own literal. Closing that needs a shared constant in F07's slice, which is F07's to add.
    /// </remarks>
    [Fact]
    public void Endpoint_ResolvesTheLocationF07Emits()
    {
        Guid submissionId = Guid.NewGuid();

        string location = LocationEmittedBySaveDailyAttendance(submissionId);

        // The Location is absolute-path form; the route pattern is not. Strip the one leading slash
        // and substitute the route parameter, then the two must be the same string.
        string pattern = Single("api/v1/attendance-submissions/{submissionId:guid}").RoutePattern.RawText!;

        Assert.Equal(
            location.TrimStart('/'),
            pattern.Replace("{submissionId:guid}", submissionId.ToString(), StringComparison.Ordinal));
    }

    /// <summary>A guard on the guards: two routes, each mapped exactly once.</summary>
    [Fact]
    public void Endpoints_AreTwoAndNoMore() => Assert.Equal(2, SubmissionRoutes().Length);

    /// <summary>
    ///     The list route is school-nested and the detail route is not.
    /// </summary>
    /// <remarks>
    ///     Conventions §1: the detail route is the <c>Location</c> target, so nesting it under a school
    ///     would mean F07 had to emit a two-segment path and every client had to carry the school id
    ///     around to re-read what it just created.
    /// </remarks>
    [Fact]
    public void Endpoint_DetailRouteIsNotNestedUnderSchools()
    {
        Assert.DoesNotContain(
            SubmissionRoutes(),
            route => (route.RoutePattern.RawText ?? string.Empty).Contains("schools", StringComparison.Ordinal)
                     && (route.RoutePattern.RawText ?? string.Empty).Contains("{submissionId", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Reconstructs the <c>Location</c> F07's endpoint writes, from F07's own constant shape.
    /// </summary>
    private static string LocationEmittedBySaveDailyAttendance(Guid submissionId) =>
        FormattableString.Invariant($"/api/v1/attendance-submissions/{submissionId}");

    private static RouteEndpoint[] SubmissionRoutes() =>
        MappedRoutes()
            .Where(route => (route.RoutePattern.RawText ?? string.Empty)
                .Contains("attendance-submissions", StringComparison.Ordinal))
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
