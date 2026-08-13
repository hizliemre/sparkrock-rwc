using System.Diagnostics;
using System.Text;
using System.Text.Json;
using api.Errors;
using domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace features.tests.Errors;

public sealed class ProblemDetailsDefaultsTests
{
    [Theory]
    [InlineData("ATTENDANCE.SUBMISSION_REJECTED", "https://sparkrock.example/errors/attendance-submission-rejected")]
    [InlineData("SYSTEM.NOT_FOUND", "https://sparkrock.example/errors/system-not-found")]
    [InlineData("VALIDATION.FAILED", "https://sparkrock.example/errors/validation-failed")]
    public void ToTypeUri_LowercasesAndHyphenates(string errorCode, string expected)
    {
        Assert.Equal(expected, ProblemDetailsDefaults.ToTypeUri(errorCode));
    }

    /// <summary>
    ///     Per status, never per handler — otherwise the same 400 gets a different title depending on
    ///     which code path produced it.
    /// </summary>
    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(500)]
    public void TitleFor_ReturnsANonEmptyTitleForEveryMappedStatus(int status)
    {
        Assert.False(string.IsNullOrWhiteSpace(ProblemDetailsDefaults.TitleFor(status)));
    }

    [Theory]
    [InlineData(400, "SYSTEM.MALFORMED_REQUEST")]
    [InlineData(403, "SYSTEM.FORBIDDEN")]
    [InlineData(404, "SYSTEM.NOT_FOUND")]
    [InlineData(405, "SYSTEM.METHOD_NOT_ALLOWED")]
    [InlineData(415, "SYSTEM.UNSUPPORTED_MEDIA_TYPE")]
    [InlineData(500, "SYSTEM.UNEXPECTED")]
    [InlineData(503, "SYSTEM.UNEXPECTED")]
    public void DefaultErrorCodeFor_MatchesTheStatusTable(int status, string expected)
    {
        Assert.Equal(expected, ProblemDetailsDefaults.DefaultErrorCodeFor(status));
    }

    [Fact]
    public void DefaultErrorCodeFor_UsesTheDeclaredConstants()
    {
        Assert.Equal(ErrorCodes.System.NotFound, ProblemDetailsDefaults.DefaultErrorCodeFor(404));
        Assert.Equal(ErrorCodes.System.Unexpected, ProblemDetailsDefaults.DefaultErrorCodeFor(500));
    }
}

/// <summary>
///     The callback itself, driven through the real <c>IProblemDetailsService</c>.
/// </summary>
/// <remarks>
///     Asserting on the serialised output rather than on the mutated object is deliberate. The
///     framework writer applies its own defaults for <c>Type</c> and <c>Title</c> <em>before</em>
///     invoking the callback, so a test that calls <c>Customize</c> against a bare
///     <see cref="ProblemDetails" /> would pass for an implementation using <c>??=</c> — which is
///     exactly the bug the unconditional assignment exists to prevent. Running the whole pipeline is
///     what makes the assertion mean something.
/// </remarks>
public sealed class ProblemDetailsCustomizeTests
{
    private static ServiceProvider Provider()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddProblemDetails(options => options.CustomizeProblemDetails = ProblemDetailsDefaults.Customize);

        return services.BuildServiceProvider();
    }

    private static async Task<JsonDocument> WriteAsync(int status, ProblemDetails? problemDetails = null)
    {
        using ServiceProvider provider = Provider();

        DefaultHttpContext httpContext = new() { RequestServices = provider };
        httpContext.Response.Body = new MemoryStream();
        httpContext.Response.StatusCode = status;

        ProblemDetails details = problemDetails ?? new ProblemDetails();
        details.Status ??= status;

        bool written = await provider.GetRequiredService<IProblemDetailsService>().TryWriteAsync(
            new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = details });

        Assert.True(written);

        httpContext.Response.Body.Position = 0;

        using StreamReader reader = new(httpContext.Response.Body, Encoding.UTF8, leaveOpen: true);

        return JsonDocument.Parse(await reader.ReadToEndAsync());
    }

    /// <summary>
    ///     Set-if-absent. The callback runs on every write, including one a handler already populated
    ///     with a specific area code, and overwriting would replace it with a status default.
    /// </summary>
    [Fact]
    public async Task Customize_WhenAHandlerAlreadySetTheErrorCode_KeepsIt()
    {
        ProblemDetails details = new()
        {
            Status = StatusCodes.Status400BadRequest,
            Extensions = { ["errorCode"] = "ATTENDANCE.SUBMISSION_REJECTED" }
        };

        using JsonDocument document = await WriteAsync(StatusCodes.Status400BadRequest, details);

        Assert.Equal(
            "ATTENDANCE.SUBMISSION_REJECTED",
            document.RootElement.GetProperty("errorCode").GetString());
    }

    [Theory]
    [InlineData(404, "SYSTEM.NOT_FOUND")]
    [InlineData(405, "SYSTEM.METHOD_NOT_ALLOWED")]
    [InlineData(415, "SYSTEM.UNSUPPORTED_MEDIA_TYPE")]
    [InlineData(500, "SYSTEM.UNEXPECTED")]
    public async Task Customize_WhenNoErrorCodeIsPresent_StampsTheStatusDefault(int status, string expected)
    {
        using JsonDocument document = await WriteAsync(status);

        Assert.Equal(expected, document.RootElement.GetProperty("errorCode").GetString());
    }

    /// <summary>
    ///     A non-string under <c>errorCode</c> used to be hard-cast inside error handling, so a
    ///     mistake in one handler surfaced as a second failure while the first was being reported.
    ///     It must degrade to the status default instead, and the wire value must still be a string —
    ///     the client branches on it.
    /// </summary>
    [Fact]
    public async Task Customize_WhenTheErrorCodeIsNotAString_FallsBackToTheStatusDefault()
    {
        ProblemDetails details = new()
        {
            Status = StatusCodes.Status404NotFound,
            Extensions = { ["errorCode"] = 42 }
        };

        using JsonDocument document = await WriteAsync(StatusCodes.Status404NotFound, details);

        Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("errorCode").ValueKind);
        Assert.Equal("SYSTEM.NOT_FOUND", document.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(
            ProblemDetailsDefaults.ToTypeUri("SYSTEM.NOT_FOUND"),
            document.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Customize_WhenTheErrorCodeIsAnEmptyString_FallsBackToTheStatusDefault()
    {
        ProblemDetails details = new()
        {
            Status = StatusCodes.Status404NotFound,
            Extensions = { ["errorCode"] = "  " }
        };

        using JsonDocument document = await WriteAsync(StatusCodes.Status404NotFound, details);

        Assert.Equal("SYSTEM.NOT_FOUND", document.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Customize_PopulatesTraceId()
    {
        using JsonDocument document = await WriteAsync(StatusCodes.Status404NotFound);

        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("traceId").GetString()));
    }

    /// <summary>
    ///     The ambient activity id is preferred over the connection-scoped request identifier, so the
    ///     value a client quotes can be found in a trace.
    /// </summary>
    [Fact]
    public async Task Customize_WhenAnActivityIsCurrent_UsesItsId()
    {
        using Activity activity = new(nameof(Customize_WhenAnActivityIsCurrent_UsesItsId));
        activity.SetIdFormat(ActivityIdFormat.W3C);
        activity.Start();

        try
        {
            using JsonDocument document = await WriteAsync(StatusCodes.Status404NotFound);

            Assert.Equal(activity.Id, document.RootElement.GetProperty("traceId").GetString());
        }
        finally
        {
            activity.Stop();
        }
    }

    /// <summary>
    ///     Assigned unconditionally, not with <c>??=</c>. The framework writer runs its own defaults
    ///     first, so both members are already populated with an RFC link and a generic title by the
    ///     time the callback sees them, and a null-coalescing assignment would silently never fire.
    /// </summary>
    [Fact]
    public async Task Customize_OverwritesTheFrameworkTypeAndTitle()
    {
        using JsonDocument document = await WriteAsync(StatusCodes.Status404NotFound);

        Assert.Equal(
            ProblemDetailsDefaults.ToTypeUri("SYSTEM.NOT_FOUND"),
            document.RootElement.GetProperty("type").GetString());

        Assert.Equal(
            ProblemDetailsDefaults.TitleFor(StatusCodes.Status404NotFound),
            document.RootElement.GetProperty("title").GetString());

        Assert.DoesNotContain(
            "rfc",
            document.RootElement.GetProperty("type").GetString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Customize_WhenTheStatusIsNotSetOnTheProblemDetails_ReadsItFromTheResponse()
    {
        using ServiceProvider provider = Provider();

        DefaultHttpContext httpContext = new() { RequestServices = provider };
        httpContext.Response.Body = new MemoryStream();
        httpContext.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;

        await provider.GetRequiredService<IProblemDetailsService>().TryWriteAsync(
            new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = new ProblemDetails() });

        httpContext.Response.Body.Position = 0;

        using StreamReader reader = new(httpContext.Response.Body, Encoding.UTF8, leaveOpen: true);
        using JsonDocument document = JsonDocument.Parse(await reader.ReadToEndAsync());

        Assert.Equal("SYSTEM.METHOD_NOT_ALLOWED", document.RootElement.GetProperty("errorCode").GetString());
    }

    /// <summary>
    ///     Server-side detail on a 5xx is exactly the text that carries connection strings and
    ///     parameter values into a client response.
    /// </summary>
    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Customize_WhenTheStatusIsServerError_StripsDetail(int status)
    {
        ProblemDetails details = new()
        {
            Status = status,
            Detail = "Npgsql failed to connect to Host=prod-db-01;Password=hunter2."
        };

        using JsonDocument document = await WriteAsync(status, details);

        Assert.False(document.RootElement.TryGetProperty("detail", out JsonElement detail) && detail.ValueKind
            is not JsonValueKind.Null);
    }

    /// <summary>
    ///     Detail is the whole payload of a 404 or a 409, so stripping it below 500 would empty them.
    /// </summary>
    [Theory]
    [InlineData(404)]
    [InlineData(409)]
    public async Task Customize_WhenTheStatusIsBelowServerError_KeepsDetail(int status)
    {
        ProblemDetails details = new() { Status = status, Detail = "The requested resource was not found." };

        using JsonDocument document = await WriteAsync(status, details);

        Assert.Equal(
            "The requested resource was not found.",
            document.RootElement.GetProperty("detail").GetString());
    }
}
