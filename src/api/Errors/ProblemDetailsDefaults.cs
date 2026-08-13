using System.Diagnostics;
using domain.Exceptions;
using Microsoft.AspNetCore.Http;

namespace api.Errors;

/// <summary>
///     Stamps every error response with one envelope, including the ones no handler ever sees.
/// </summary>
/// <remarks>
///     Registered through <c>AddProblemDetails</c> and paired with <c>UseStatusCodePages</c>. The
///     callback alone does not cover routing 404s, 405s, 415s or minimal-API binding failures — those
///     never reach an <c>IExceptionHandler</c>, so without the middleware a client sees two different
///     error shapes from the same API.
/// </remarks>
internal static class ProblemDetailsDefaults
{
    public const string TypeUriPrefix = "https://sparkrock.example/errors/";

    /// <summary>
    ///     Fills in what is missing, and never overwrites what a handler already set.
    /// </summary>
    /// <remarks>
    ///     Set-if-absent matters: this runs on <em>every</em> ProblemDetails write, including those a
    ///     handler populated with a specific area code. Overwriting would replace
    ///     <c>ATTENDANCE.UNKNOWN_CODE</c> with a generic status default.
    /// </remarks>
    public static void Customize(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        int status = context.ProblemDetails.Status ?? context.HttpContext.Response.StatusCode;

        context.ProblemDetails.Extensions["traceId"] =
            Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;

        if (!context.ProblemDetails.Extensions.TryGetValue("errorCode", out object? existing)
            || existing is null)
        {
            context.ProblemDetails.Extensions["errorCode"] = DefaultErrorCodeFor(status);
        }

        string errorCode = context.ProblemDetails.Extensions["errorCode"] as string
                           ?? DefaultErrorCodeFor(status);

        // Assigned unconditionally, not with ??=. DefaultProblemDetailsWriter runs the framework's
        // own defaults before this callback, so Type and Title are already populated with RFC links
        // and generic titles by the time we see them — a null-coalescing assignment silently never
        // fires and the documented envelope never reaches the wire. Neither handler sets these, and
        // titles are per status rather than per handler, so overwriting is correct.
        context.ProblemDetails.Type = ToTypeUri(errorCode);
        context.ProblemDetails.Title = TitleFor(status);

        // Server-side detail on a 5xx is exactly the text that leaks connection strings and
        // parameter values into a client response.
        if (status >= StatusCodes.Status500InternalServerError)
            context.ProblemDetails.Detail = null;
    }

    /// <summary>Maps an error code to a stable documentation URI.</summary>
    public static string ToTypeUri(string errorCode)
    {
        ArgumentNullException.ThrowIfNull(errorCode);

        return TypeUriPrefix + errorCode.ToLowerInvariant().Replace('.', '-').Replace('_', '-');
    }

    /// <summary>Titles are per status, never per handler.</summary>
    public static string TitleFor(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "The request could not be processed.",
        StatusCodes.Status403Forbidden => "The operation requires a privilege you do not have.",
        StatusCodes.Status404NotFound => "The requested resource was not found.",
        StatusCodes.Status405MethodNotAllowed => "The method is not allowed for this resource.",
        StatusCodes.Status409Conflict => "The request conflicts with the current state.",
        StatusCodes.Status415UnsupportedMediaType => "The media type is not supported.",
        _ => status >= StatusCodes.Status500InternalServerError
            ? "An unexpected error occurred."
            : "The request could not be processed."
    };

    public static string DefaultErrorCodeFor(int status) => status switch
    {
        StatusCodes.Status400BadRequest => ErrorCodes.System.MalformedRequest,
        StatusCodes.Status403Forbidden => ErrorCodes.System.Forbidden,
        StatusCodes.Status404NotFound => ErrorCodes.System.NotFound,
        StatusCodes.Status405MethodNotAllowed => ErrorCodes.System.MethodNotAllowed,
        StatusCodes.Status415UnsupportedMediaType => ErrorCodes.System.UnsupportedMediaType,
        _ => ErrorCodes.System.Unexpected
    };
}
