using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace api.Errors;

/// <summary>
///     Writes the error envelope and reports the response handled, even when content negotiation
///     declines it.
/// </summary>
/// <remarks>
///     <c>IProblemDetailsService.TryWriteAsync</c> returns false when no registered writer will accept
///     the request: <c>DefaultProblemDetailsWriter.CanWrite</c> requires the <c>Accept</c> header to
///     be absent or to name a JSON-compatible media type. Returning that value straight out of an
///     <c>IExceptionHandler</c> reports "not handled", and the next thing in the pipeline is
///     <c>WebApplication</c>'s auto-registered <c>DeveloperExceptionPageMiddleware</c>, which serves
///     the exception, its stack trace and the request headers as HTML. A client sending
///     <c>Accept: text/plain</c> turns a routine 404 into a 500 stack-trace page — every detail of the
///     failure disclosed by a header the client chooses.
///     <para>
///         A failed negotiation is not a failure to handle. The envelope is the only shape this API
///         emits, so it is written directly rather than negotiated away, using the same serializer
///         options and the same customisation callback the framework writer would have used — the two
///         paths must not produce different bodies for the same error.
///     </para>
/// </remarks>
internal static class ProblemDetailsEnvelope
{
    public const string ContentType = "application/problem+json";

    /// <summary>Used only when the context has no service provider, as in a unit test.</summary>
    private static readonly JsonSerializerOptions FallbackSerializerOptions = new(JsonSerializerDefaults.Web);

    public static async ValueTask<bool> WriteAsync(
        IProblemDetailsService problemDetailsService,
        ProblemDetailsContext context)
    {
        if (await problemDetailsService.TryWriteAsync(context))
            return true;

        HttpContext httpContext = context.HttpContext;

        // The framework writer applies the customisation itself, so on this path it has not run.
        ProblemDetailsDefaults.Customize(context);

        await httpContext.Response.WriteAsJsonAsync(
            context.ProblemDetails,
            SerializerOptions(httpContext),
            ContentType,
            httpContext.RequestAborted);

        return true;
    }

    private static JsonSerializerOptions SerializerOptions(HttpContext httpContext) =>
        httpContext.RequestServices?.GetService<IOptions<JsonOptions>>()?.Value.SerializerOptions
        ?? FallbackSerializerOptions;
}
