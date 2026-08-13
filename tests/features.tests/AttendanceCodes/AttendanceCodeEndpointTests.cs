using Carter;
using domain.Exceptions;
using features;
using infra.persistence.postgre.ErrorTranslation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace features.tests.AttendanceCodes;

/// <summary>
///     Acceptance criterion 1 and T03-10: the five routes, their names, their tag, and one
///     <c>.ProducesProblem</c> per row of spec §6 — <b>exactly</b>, not a superset.
/// </summary>
/// <remarks>
///     A superset is not harmless. <c>.ProducesProblem</c> is the OpenAPI document a client generates
///     from, so a documented 409 on a route that cannot raise one produces a branch in generated code
///     that is dead by construction, and a missing one produces a status the client will not handle.
///     Reviewing them by eye across five routes and twelve workstreams is not a mechanism.
/// </remarks>
public sealed class AttendanceCodeEndpointTests
{
    public static TheoryData<string, string, string, int, int[]> Routes() => new()
    {
        { "GET", "api/v1/attendance-codes", "GetAttendanceCodes", 200, [400] },
        { "POST", "api/v1/attendance-codes", "CreateAttendanceCode", 201, [400, 403, 409] },
        { "GET", "api/v1/attendance-codes/{codeId}", "GetAttendanceCodeById", 200, [404] },
        { "PUT", "api/v1/attendance-codes/{codeId}", "UpdateAttendanceCode", 200, [400, 403, 404] },
        { "DELETE", "api/v1/attendance-codes/{codeId}", "DeactivateAttendanceCode", 204, [403, 404] }
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public void Endpoint_DeclaresExactlyTheDocumentedStatuses(
        string method,
        string path,
        string name,
        int successStatus,
        int[] problemStatuses)
    {
        RouteEndpoint endpoint = Single(method, path);

        Assert.Equal(name, endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName);

        Assert.Contains(
            "AttendanceCodes",
            endpoint.Metadata.GetMetadata<ITagsMetadata>()?.Tags ?? []);

        int[] declared = endpoint.Metadata
            .OfType<IProducesResponseTypeMetadata>()
            .Select(metadata => metadata.StatusCode)
            .Order()
            .ToArray();

        Assert.Equal([successStatus, .. problemStatuses.Order()], declared);
    }

    /// <summary>
    ///     A guard on the guard. If discovery returned nothing, every assertion above would fail with a
    ///     message about a missing route rather than about a missing status — but a future filter bug
    ///     that returned the wrong endpoints would not be visible at all.
    /// </summary>
    [Fact]
    public void Endpoints_AreFiveAndNoMore() =>
        Assert.Equal(5, MappedRoutes().Count(route =>
            (route.RoutePattern.RawText ?? string.Empty).StartsWith(
                "api/v1/attendance-codes", StringComparison.Ordinal)));

    private static RouteEndpoint Single(string method, string path) =>
        Assert.Single(
            MappedRoutes(),
            route => string.Equals(route.RoutePattern.RawText, path, StringComparison.Ordinal)
                     && route.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));

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

/// <summary>
///     F03 is the first consumer of the <c>ix_attendance_codes_value</c> registry row, so a typo in
///     either half surfaces here for the first time — as a 500 rather than a 409.
/// </summary>
/// <remarks>
///     F03's spec and T03-01 both state F01c already shipped this row. It had not: the constraint, the
///     index and conventions §5's table entry all existed, and nothing mapped the one to the other.
///     Nothing failed, because <c>TryResolve</c> is an ordinal lookup over a dictionary and a missing
///     key is a miss rather than an error — the same silent-miss shape <c>ConstraintRegistryTests</c>
///     documents in the opposite direction.
///     <para>
///         This asserts the mapping exists and says the right thing.
///         <c>ConstraintRegistryTests.Registry_KeyNamesAnIndexTheSchemaCreates</c> already asserts the
///         key names a real unique index. Neither proves the <c>23505</c> path end to end; that is the
///         integration tier's, and until it runs the 409 is a claim.
///     </para>
/// </remarks>
public sealed class AttendanceCodeConstraintRegistryTests
{
    /// <summary>
    ///     The exact index name, spelled out rather than read from
    ///     <c>SchemaConstraintErrors.Names</c>.
    /// </summary>
    /// <remarks>
    ///     Asserting through the constant proves only that the dictionary is keyed by whatever the
    ///     constant says. The key has to match the name Postgres reports on a <c>23505</c>, which comes
    ///     from <c>HasDatabaseName</c> in <c>AttendanceCodeConfiguration</c> — so a typo that moved both
    ///     the constant and the mapping together would be invisible to a test that read the constant.
    ///     <c>ConstraintRegistryTests.Registry_KeyNamesAnIndexTheSchemaCreates</c> checks the other
    ///     direction, against the model.
    /// </remarks>
    private const string ValueIndexName = "ix_attendance_codes_value";

    [Fact]
    public void Registry_MapsTheValueIndexToDuplicateValue()
    {
        Assert.Equal(ValueIndexName, SchemaConstraintErrors.Names.AttendanceCodeValue);

        Assert.True(
            SchemaConstraintErrors.Mappings.TryGetValue(ValueIndexName, out ConstraintErrorMapping? mapping),
            "ix_attendance_codes_value is unmapped, so a duplicate value escapes as a raw "
            + "DbUpdateException — a 500 carrying a Postgres message instead of the 409 spec §6 "
            + "documents.");

        Assert.Equal(ErrorCodes.AttendanceCode.DuplicateValue, mapping!.ErrorCode);
    }

    /// <summary>
    ///     Not retryable. The value is caller-supplied, so every attempt collides identically and a
    ///     retry burns DEC-14's whole attempt bound to return the same error.
    /// </summary>
    [Fact]
    public void Registry_MarksTheValueIndexNotRetryable() =>
        Assert.False(SchemaConstraintErrors.Mappings[ValueIndexName].Retryable);
}
