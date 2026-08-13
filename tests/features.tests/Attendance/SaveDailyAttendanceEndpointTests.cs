using System.Text;
using System.Text.Json;
using Carter;
using features.Attendance;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace features.tests.Attendance;

/// <summary>
///     The route, its declared statuses, and the two bindings that are not visible in metadata.
/// </summary>
/// <remarks>
///     The endpoint is driven for real — its <c>RequestDelegate</c> is invoked against a
///     <see cref="DefaultHttpContext" /> with a stub mediator — because the header binding and the
///     <c>Location</c> header cannot be asserted from metadata, and those are exactly the two things
///     spec §6 and O-01 pin.
/// </remarks>
public sealed class SaveDailyAttendanceEndpointTests
{
    private const string RoutePattern = "api/v1/schools/{schoolId}/attendance/{date}/submissions";

    /// <summary>
    ///     Group-relative, so <c>UseSparkrockRwc</c>'s <c>MapGroup("api/v1")</c> supplies the prefix.
    ///     Writing <c>/api/...</c> in the module would double it.
    /// </summary>
    [Fact]
    public void Endpoint_MapsThePathFromConventionsSectionOne()
    {
        RouteEndpoint endpoint = Single();

        Assert.Equal(nameof(SaveDailyAttendance), endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName);
        Assert.Contains("Attendance", endpoint.Metadata.GetMetadata<ITagsMetadata>()?.Tags ?? []);
        Assert.Contains("POST", endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
    }

    /// <summary>
    ///     Exactly the statuses spec §7 documents, not a superset: <c>.ProducesProblem</c> is the
    ///     OpenAPI a client generates from, so a status the route cannot raise becomes a branch in
    ///     generated code that is dead by construction.
    /// </summary>
    [Fact]
    public void Endpoint_DeclaresExactlyTheDocumentedStatuses()
    {
        int[] declared = Single().Metadata
            .OfType<IProducesResponseTypeMetadata>()
            .Select(metadata => metadata.StatusCode)
            .Order()
            .ToArray();

        Assert.Equal(
            [
                StatusCodes.Status201Created,
                StatusCodes.Status400BadRequest,
                StatusCodes.Status404NotFound,
                StatusCodes.Status409Conflict
            ],
            declared);
    }

    /// <summary>
    ///     The route-value key must be <c>date</c>, matching <c>Command.Date</c>: <c>ViolationSource</c>
    ///     resolves a violation's <c>source</c> by matching the camel-cased property root against route
    ///     values, so renaming either half silently reports a malformed path segment as a body failure.
    /// </summary>
    [Fact]
    public void Endpoint_NamesTheDateRouteValueDate()
    {
        Assert.Contains(
            Single().RoutePattern.Parameters,
            parameter => string.Equals(parameter.Name, "date", StringComparison.Ordinal));
    }

    /// <summary>
    ///     No <c>:datetime</c> or <c>:guid</c> constraint on either segment — a constraint turns a
    ///     malformed value into a routing 404 with <c>SYSTEM.NOT_FOUND</c>, indistinguishable from an
    ///     unknown school, which is the outcome binding the date as a string exists to prevent.
    /// </summary>
    [Fact]
    public void Endpoint_ConstrainsNeitherRouteSegment()
    {
        Assert.Equal(RoutePattern, Single().RoutePattern.RawText);
    }

    [Fact]
    public async Task Endpoint_BindsTheIdempotencyKeyHeader()
    {
        StubMediator mediator = new();

        await InvokeAsync(mediator, idempotencyKey: "8f14e45fceea167a5a36dedd4bea2543");

        Assert.Equal("8f14e45fceea167a5a36dedd4bea2543", mediator.Command!.IdempotencyKey);
    }

    /// <summary>Absent means absent, not empty — the filtered index permits many nulls (O-09).</summary>
    [Fact]
    public async Task Endpoint_WhenTheIdempotencyKeyHeaderIsAbsent_BindsNull()
    {
        StubMediator mediator = new();

        await InvokeAsync(mediator, idempotencyKey: null);

        Assert.Null(mediator.Command!.IdempotencyKey);
    }

    /// <summary>Route values are authoritative; the body carries neither <c>schoolId</c> nor <c>date</c>.</summary>
    [Fact]
    public async Task Endpoint_BindsTheRouteValuesAndTheBody()
    {
        StubMediator mediator = new();

        Guid schoolId = Guid.NewGuid();
        Guid studentId = Guid.NewGuid();

        await InvokeAsync(
            mediator,
            schoolId: schoolId,
            date: "2026-09-14",
            body: $$"""{"entries":[{"studentId":"{{studentId}}","attendCode":"A","minutesLate":12,"notes":"Late bus"}]}""");

        Assert.Equal(schoolId, mediator.Command!.SchoolId);
        Assert.Equal("2026-09-14", mediator.Command.Date);

        SaveDailyAttendance.Entry entry = Assert.Single(mediator.Command.Entries);
        Assert.Equal(studentId, entry.StudentId);
        Assert.Equal("A", entry.AttendCode);
        Assert.Equal(12, entry.MinutesLate);
        Assert.Equal("Late bus", entry.Notes);
    }

    /// <summary>
    ///     An absent body binds to an empty entry list, so the answer is the validator's
    ///     <c>VALIDATION.REQUIRED_FIELD</c> 400 rather than a binder-level malformed-request one.
    /// </summary>
    [Fact]
    public async Task Endpoint_WhenTheBodyHasNoEntries_SendsAnEmptyList()
    {
        StubMediator mediator = new();

        await InvokeAsync(mediator, body: "{}");

        Assert.Empty(mediator.Command!.Entries);
    }

    /// <summary>
    ///     <b>O-01.</b> The target is the submission log entry, whose rows F11 enumerates through
    ///     <c>StudentAttendance.SubmissionId</c>. It is written with the <c>api/v1</c> prefix because
    ///     <c>Location</c> is an absolute path, not a group-relative one.
    /// </summary>
    [Fact]
    public async Task Endpoint_ReturnsLocationPointingAtTheSubmission()
    {
        StubMediator mediator = new();

        HttpContext context = await InvokeAsync(mediator);

        Assert.Equal(StatusCodes.Status201Created, context.Response.StatusCode);
        Assert.Equal(
            $"/api/v1/attendance-submissions/{mediator.Response!.SubmissionId}",
            context.Response.Headers.Location.ToString());
    }

    // ------------------------------------------------------------------------------- helpers

    private static async Task<HttpContext> InvokeAsync(
        StubMediator mediator,
        Guid? schoolId = null,
        string date = "2026-09-14",
        string? idempotencyKey = null,
        string? body = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        builder.Services.AddMediatR(configuration =>
            configuration.RegisterServicesFromAssembly(typeof(features.ServiceExtensions).Assembly));
        builder.Services.AddCarter(new DependencyContextAssemblyCatalog([typeof(features.ServiceExtensions).Assembly]));

        // Registered last, so it is what GetRequiredService resolves. Nothing below reaches a handler.
        builder.Services.AddSingleton<IMediator>(mediator);

        await using WebApplication app = builder.Build();
        app.UseSparkrockRwc();

        RouteEndpoint endpoint = Routes(app).Single(
            route => string.Equals(route.RoutePattern.RawText, RoutePattern, StringComparison.Ordinal));

        DefaultHttpContext context = new() { RequestServices = app.Services };

        // DefaultHttpContext ships no IHttpRequestBodyDetectionFeature, and without one ASP.NET
        // decides the request cannot have a body and never reads it — the body parameter binds to null
        // and every assertion about it passes vacuously. A real server supplies this feature.
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new RequestCanHaveBody());

        context.Request.Method = HttpMethods.Post;
        context.Request.RouteValues["schoolId"] = (schoolId ?? Guid.NewGuid()).ToString();
        context.Request.RouteValues["date"] = date;
        context.Request.ContentType = "application/json";

        if (idempotencyKey is not null)
            context.Request.Headers[SaveDailyAttendance.IdempotencyKeyHeader] = idempotencyKey;

        byte[] payload = Encoding.UTF8.GetBytes(
            body ?? $$"""{"entries":[{"studentId":"{{Guid.NewGuid()}}","attendCode":"A"}]}""");

        context.Request.Body = new MemoryStream(payload);
        context.Request.ContentLength = payload.Length;
        context.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(context);

        return context;
    }

    private static RouteEndpoint Single()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        builder.Services.AddMediatR(configuration =>
            configuration.RegisterServicesFromAssembly(typeof(features.ServiceExtensions).Assembly));
        builder.Services.AddCarter(new DependencyContextAssemblyCatalog([typeof(features.ServiceExtensions).Assembly]));

        WebApplication app = builder.Build();
        app.UseSparkrockRwc();

        return Assert.Single(
            Routes(app),
            route => string.Equals(route.RoutePattern.RawText, RoutePattern, StringComparison.Ordinal));
    }

    private static RouteEndpoint[] Routes(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources
        .SelectMany(source => source.Endpoints)
        .OfType<RouteEndpoint>()
        .ToArray();

    private sealed class RequestCanHaveBody : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    /// <summary>
    ///     Captures the command the endpoint built and answers with a canned response.
    /// </summary>
    /// <remarks>
    ///     Conventions §6 bans mocking packages. Only <c>Send&lt;TResponse&gt;</c> is reachable from
    ///     this route; the rest throw rather than returning a default, so a future edit that started
    ///     using one of them fails loudly instead of silently doing nothing.
    /// </remarks>
    private sealed class StubMediator : IMediator
    {
        public SaveDailyAttendance.Command? Command { get; private set; }

        public SaveDailyAttendance.Response? Response { get; private set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Command = (SaveDailyAttendance.Command)(object)request!;

            Response = new SaveDailyAttendance.Response
            {
                SubmissionId = Guid.NewGuid(),
                SchoolId = Command.SchoolId,
                AttendanceDate = new DateOnly(2026, 9, 14),
                SchoolYear = 2026,
                SchoolYearLabel = "2026-2027",
                TermId = null,
                SubmittedAt = new DateTimeOffset(2026, 9, 14, 12, 31, 0, TimeSpan.Zero),
                SubmittedBy = new SaveDailyAttendance.Response.SubmittedByInfo
                {
                    UserId = Guid.Empty,
                    DisplayName = "Stub User"
                },
                RecordCount = Command.Entries.Count,
                CreatedCount = Command.Entries.Count,
                UpdatedCount = 0,
                Entries = [],
                Alerts = new SaveDailyAttendance.Response.AlertSummary { Raised = [], Resolved = [] }
            };

            return Task.FromResult((TResponse)(object)Response);
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
            IStreamRequest<TResponse> request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification =>
            throw new NotSupportedException();
    }
}
