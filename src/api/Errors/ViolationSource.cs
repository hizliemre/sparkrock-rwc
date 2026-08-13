using Microsoft.AspNetCore.Http;

namespace api.Errors;

/// <summary>
///     Decides the <c>source</c> member of a violation from the request that produced it.
/// </summary>
/// <remarks>
///     <c>source</c> ∈ <c>body</c> | <c>path</c> | <c>query</c> | <c>header</c> exists so a malformed
///     <c>{date}</c> route value and a body field of the same name are distinguishable
///     (conventions §2). Writing <c>body</c> unconditionally makes the member decorative.
///     <para>
///         The correct input is binding metadata — which parameter of the endpoint the validated
///         property was bound from. That is not reachable from here: requests are plain MediatR
///         messages assembled inside <c>features</c>, carry no binding annotations, and the exception
///         arrives at the handler with nothing but a property path. So this infers, in the order
///         below, and the residual gap is recorded as an open finding rather than papered over.
///     </para>
///     <list type="number">
///         <item>the root path segment names a route value → <c>path</c>. Route values are
///         authoritative and a body must not repeat them, so a name collision resolves this way.</item>
///         <item>it names a query key → <c>query</c>.</item>
///         <item>the request carries a body → <c>body</c>.</item>
///         <item>otherwise <c>query</c>: a violation cannot have come from a body that was never
///         sent, and that specific lie — <c>"source": "body"</c> on a paged GET — is what this
///         replaces.</item>
///     </list>
///     <para>
///         <c>header</c> is never inferred. Nothing in the route table validates a header, and
///         guessing one from a property name would be inventing information.
///     </para>
/// </remarks>
internal static class ViolationSource
{
    public const string Body = "body";

    public const string Path = "path";

    public const string Query = "query";

    public static string For(HttpRequest? request, string? clrPath)
    {
        if (request is null)
            return Body;

        string root = RootSegment(clrPath);

        if (root.Length > 0)
        {
            if (request.RouteValues.ContainsKey(root))
                return Path;

            if (request.Query.ContainsKey(root))
                return Query;
        }

        return HasBody(request) ? Body : Query;
    }

    /// <summary>
    ///     The first dot-segment, without its indexer, camel-cased the way the payload key is.
    /// </summary>
    /// <remarks>
    ///     <c>Entries[3].AttendCode</c> is a failure about <c>entries</c> as far as the request is
    ///     concerned; the nested part cannot be a query key or a route value.
    /// </remarks>
    private static string RootSegment(string? clrPath)
    {
        if (string.IsNullOrEmpty(clrPath))
            return string.Empty;

        int dot = clrPath.IndexOf('.', StringComparison.Ordinal);
        string segment = dot < 0 ? clrPath : clrPath[..dot];

        int bracket = segment.IndexOf('[', StringComparison.Ordinal);

        if (bracket >= 0)
            segment = segment[..bracket];

        return ViolationPath.ToCamelCase(segment);
    }

    /// <summary>
    ///     Whether the request declared a body at all.
    /// </summary>
    /// <remarks>
    ///     Content-Length is absent under chunked transfer encoding, so a declared content type counts
    ///     on its own. Both absent means no body was sent, whatever the method allows.
    /// </remarks>
    private static bool HasBody(HttpRequest request) =>
        request.ContentLength > 0 || !string.IsNullOrEmpty(request.ContentType);
}
