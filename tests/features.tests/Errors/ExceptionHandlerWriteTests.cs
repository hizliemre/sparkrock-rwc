using System.Text;
using System.Text.Json;
using api.Errors;
using domain.Exceptions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace features.tests.Errors;

/// <summary>
///     What the client receives when the framework's ProblemDetails writer declines the response.
/// </summary>
/// <remarks>
///     <c>DefaultProblemDetailsWriter.CanWrite</c> returns false when the request's <c>Accept</c>
///     header names no JSON-compatible media type, so <c>TryWriteAsync</c> returns false and a handler
///     that returns that value reports "not handled". <c>WebApplication</c> auto-registers
///     <c>DeveloperExceptionPageMiddleware</c> in Development, which then serves the exception, its
///     stack trace and the request headers — turning a routine <c>NotFoundException</c> into a 500
///     stack-trace page. A failed negotiation is a reason to write the envelope anyway, not a reason
///     to hand the exception to a debugging page.
/// </remarks>
public sealed class ExceptionHandlerWriteTests
{
    private const string NonJsonAccept = "text/plain";

    private static ServiceProvider Provider()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddProblemDetails(options => options.CustomizeProblemDetails = ProblemDetailsDefaults.Customize);

        return services.BuildServiceProvider();
    }

    private static DefaultHttpContext Context(ServiceProvider provider, string? accept)
    {
        DefaultHttpContext context = new() { RequestServices = provider };
        context.Response.Body = new MemoryStream();

        if (accept is not null)
            context.Request.Headers.Accept = accept;

        return context;
    }

    private static string BodyOf(HttpContext context)
    {
        context.Response.Body.Position = 0;

        using StreamReader reader = new(context.Response.Body, Encoding.UTF8, leaveOpen: true);

        return reader.ReadToEnd();
    }

    private static ValidationException ValidationFailure() =>
        new([new ValidationFailure("TestProperty", "'Test Property' must not be empty.")]);

    [Theory]
    [InlineData(null)]
    [InlineData("application/json")]
    [InlineData(NonJsonAccept)]
    public async Task TryHandleAsync_WhenDomainExceptionIsMapped_ReportsHandledWhateverTheClientAccepts(string? accept)
    {
        using ServiceProvider provider = Provider();
        DefaultHttpContext context = Context(provider, accept);

        DomainExceptionHandler handler = new(provider.GetRequiredService<IProblemDetailsService>());

        bool handled = await handler.TryHandleAsync(
            context, new NotFoundException("SCHOOL.NOT_FOUND"), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("application/json")]
    [InlineData(NonJsonAccept)]
    public async Task TryHandleAsync_WhenValidationFails_ReportsHandledWhateverTheClientAccepts(string? accept)
    {
        using ServiceProvider provider = Provider();
        DefaultHttpContext context = Context(provider, accept);

        ValidationExceptionHandler handler = new(provider.GetRequiredService<IProblemDetailsService>());

        bool handled = await handler.TryHandleAsync(context, ValidationFailure(), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    /// <summary>
    ///     Reporting "handled" is only worth anything if something was actually written.
    /// </summary>
    [Fact]
    public async Task TryHandleAsync_WhenTheClientAcceptsNoJson_StillWritesTheEnvelope()
    {
        using ServiceProvider provider = Provider();
        DefaultHttpContext context = Context(provider, NonJsonAccept);

        DomainExceptionHandler handler = new(provider.GetRequiredService<IProblemDetailsService>());

        await handler.TryHandleAsync(context, new NotFoundException("SCHOOL.NOT_FOUND"), CancellationToken.None);

        using JsonDocument document = JsonDocument.Parse(BodyOf(context));

        Assert.Equal("SCHOOL.NOT_FOUND", document.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(404, document.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("traceId").GetString()));
        Assert.StartsWith(
            "application/problem+json",
            context.Response.ContentType ?? string.Empty,
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     The fallback path must produce the same envelope as the negotiated one, or the shape a
    ///     client sees depends on a header it may not control.
    /// </summary>
    [Fact]
    public async Task TryHandleAsync_WhenTheClientAcceptsNoJson_WritesTheSameEnvelopeAsWhenItDoes()
    {
        using ServiceProvider provider = Provider();

        DefaultHttpContext negotiated = Context(provider, "application/json");
        DefaultHttpContext fallback = Context(provider, NonJsonAccept);

        DomainExceptionHandler handler = new(provider.GetRequiredService<IProblemDetailsService>());

        await handler.TryHandleAsync(negotiated, new NotFoundException("SCHOOL.NOT_FOUND"), CancellationToken.None);
        await handler.TryHandleAsync(fallback, new NotFoundException("SCHOOL.NOT_FOUND"), CancellationToken.None);

        using JsonDocument left = JsonDocument.Parse(BodyOf(negotiated));
        using JsonDocument right = JsonDocument.Parse(BodyOf(fallback));

        Assert.Equal(
            left.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal),
            right.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

        Assert.Equal(
            left.RootElement.GetProperty("type").GetString(),
            right.RootElement.GetProperty("type").GetString());

        Assert.Equal(
            left.RootElement.GetProperty("title").GetString(),
            right.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task TryHandleAsync_WhenTheClientAcceptsNoJson_StillWritesTheViolations()
    {
        using ServiceProvider provider = Provider();
        DefaultHttpContext context = Context(provider, NonJsonAccept);

        ValidationExceptionHandler handler = new(provider.GetRequiredService<IProblemDetailsService>());

        await handler.TryHandleAsync(context, ValidationFailure(), CancellationToken.None);

        using JsonDocument document = JsonDocument.Parse(BodyOf(context));
        JsonElement violation = document.RootElement.GetProperty("violations")[0];

        Assert.Equal("VALIDATION.FAILED", document.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal("testProperty", violation.GetProperty("path").GetString());
    }

    /// <summary>
    ///     <c>ConflictException</c> takes an arbitrary message that lands in <c>detail</c> verbatim, so
    ///     that member is a free-text channel too. There is no property path to key the field-name rule
    ///     on, so the length cap is the only defence available — and it has to be applied.
    /// </summary>
    [Fact]
    public async Task TryHandleAsync_WhenADomainExceptionCarriesAnUnboundedMessage_CapsTheDetail()
    {
        using ServiceProvider provider = Provider();
        DefaultHttpContext context = Context(provider, null);

        DomainExceptionHandler handler = new(provider.GetRequiredService<IProblemDetailsService>());

        await handler.TryHandleAsync(
            context,
            new ConflictException("TERM.OVERLAP", new string('x', 5000)),
            CancellationToken.None);

        using JsonDocument document = JsonDocument.Parse(BodyOf(context));

        Assert.True(
            document.RootElement.GetProperty("detail").GetString()!.Length
            <= ViolationMessage.MaximumMessageLength);
    }

    /// <summary>
    ///     A 400 from accumulated violations has no detail, and must not acquire one.
    /// </summary>
    [Fact]
    public async Task TryHandleAsync_WhenABusinessRuleExceptionHasNoDetail_WritesNone()
    {
        using ServiceProvider provider = Provider();
        DefaultHttpContext context = Context(provider, null);

        DomainExceptionHandler handler = new(provider.GetRequiredService<IProblemDetailsService>());

        await handler.TryHandleAsync(
            context,
            new BusinessRuleException(
                "ATTENDANCE.SUBMISSION_REJECTED",
                [new Violation("body", "Entries[3].AttendCode", "ATTENDANCE.UNKNOWN_CODE", "Unknown code 'XX'.")]),
            CancellationToken.None);

        using JsonDocument document = JsonDocument.Parse(BodyOf(context));

        Assert.False(document.RootElement.TryGetProperty("detail", out JsonElement detail)
                     && detail.ValueKind is not JsonValueKind.Null);
    }

    /// <summary>
    ///     An exception neither handler recognises must stay unhandled, so the pipeline can produce the
    ///     generic 500. Swallowing it here would report success for a response nobody wrote.
    /// </summary>
    [Fact]
    public async Task TryHandleAsync_WhenTheExceptionIsUnrecognised_ReportsNotHandled()
    {
        using ServiceProvider provider = Provider();
        IProblemDetailsService problemDetails = provider.GetRequiredService<IProblemDetailsService>();

        Assert.False(await new DomainExceptionHandler(problemDetails).TryHandleAsync(
            Context(provider, null), new InvalidOperationException("boom"), CancellationToken.None));

        Assert.False(await new ValidationExceptionHandler(problemDetails).TryHandleAsync(
            Context(provider, null), new InvalidOperationException("boom"), CancellationToken.None));
    }

    /// <summary>
    ///     The violation source is inferred from the request, so the first paged endpoint does not
    ///     report a query-string rejection as a body field.
    /// </summary>
    [Fact]
    public async Task TryHandleAsync_WhenTheRejectedValueCameFromTheQueryString_ReportsSourceQuery()
    {
        using ServiceProvider provider = Provider();
        DefaultHttpContext context = Context(provider, null);
        context.Request.Method = HttpMethods.Get;
        context.Request.QueryString = new QueryString("?page=0");

        ValidationExceptionHandler handler = new(provider.GetRequiredService<IProblemDetailsService>());

        await handler.TryHandleAsync(
            context,
            new ValidationException([new ValidationFailure("Page", "'Page' must be 1 or greater.")]),
            CancellationToken.None);

        using JsonDocument document = JsonDocument.Parse(BodyOf(context));

        Assert.Equal("query", document.RootElement.GetProperty("violations")[0].GetProperty("source").GetString());
    }

    /// <summary>
    ///     Conventions §2: <c>Notes</c> never appears in a response body.
    /// </summary>
    [Fact]
    public async Task TryHandleAsync_WhenAValidatorEchoesFreeText_DoesNotWriteItToTheBody()
    {
        const string safeguarding = "Mother reports the child is being kept home following Tuesday's incident.";

        using ServiceProvider provider = Provider();
        DefaultHttpContext context = Context(provider, null);

        ValidationExceptionHandler handler = new(provider.GetRequiredService<IProblemDetailsService>());

        await handler.TryHandleAsync(
            context,
            new ValidationException([
                new ValidationFailure("Entries[3].Notes", $"You entered '{safeguarding}'.", safeguarding)
            ]),
            CancellationToken.None);

        Assert.DoesNotContain("Mother reports", BodyOf(context), StringComparison.Ordinal);
    }

    /// <summary>
    ///     A <c>BusinessRuleException</c> carries handler-authored messages, but the field-name rule has
    ///     to hold there too — nothing stops a handler interpolating the value it rejected.
    /// </summary>
    [Fact]
    public async Task TryHandleAsync_WhenABusinessRuleViolationEchoesFreeText_DoesNotWriteItToTheBody()
    {
        const string safeguarding = "Mother reports the child is being kept home following Tuesday's incident.";

        using ServiceProvider provider = Provider();
        DefaultHttpContext context = Context(provider, null);

        DomainExceptionHandler handler = new(provider.GetRequiredService<IProblemDetailsService>());

        await handler.TryHandleAsync(
            context,
            new BusinessRuleException(
                "ATTENDANCE.SUBMISSION_REJECTED",
                [new Violation("body", "Entries[3].Notes", "ATTENDANCE.NOTES_TOO_LONG", $"'{safeguarding}'")]),
            CancellationToken.None);

        Assert.DoesNotContain("Mother reports", BodyOf(context), StringComparison.Ordinal);
    }
}
