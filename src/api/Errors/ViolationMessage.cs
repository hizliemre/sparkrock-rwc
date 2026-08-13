namespace api.Errors;

/// <summary>
///     Keeps free text out of the response body.
/// </summary>
/// <remarks>
///     Conventions §2: <c>message</c> is a developer aid, <c>code</c> is the contract, and messages
///     may echo bounded structured values — a code, an index — but never free-text fields.
///     <c>Notes</c> never appears in a response body.
///     <para>
///         Prose alone does not hold that line. Several FluentValidation built-ins interpolate
///         <c>{PropertyValue}</c> into their default English, so a plain <c>MaximumLength</c> rule on
///         a notes field returns the safeguarding text to whoever sent the request — and on a 400 that
///         is not necessarily whoever is entitled to read it. The rule is therefore enforced here,
///         once, on every message either handler writes.
///     </para>
///     <para>
///         Two layers, because either alone is insufficient. The name list is authoritative but cannot
///         be complete; the length rule catches a free-text field nobody thought to name, but would
///         strip a legitimate attendance code if it were the only rule.
///     </para>
/// </remarks>
internal static class ViolationMessage
{
    /// <summary>What replaces a message that cannot be shown at all.</summary>
    public const string Redacted = "The value is not valid.";

    /// <summary>What replaces an unbounded value quoted inside an otherwise useful message.</summary>
    public const string RedactedValue = "<redacted>";

    /// <summary>
    ///     Longest attempted string value still treated as bounded.
    /// </summary>
    /// <remarks>
    ///     Sized against the bounded values the contract actually names: an attendance code, a grade,
    ///     an ISO date, a Guid (36). Anything longer is prose.
    /// </remarks>
    public const int MaximumEchoedValueLength = 40;

    /// <summary>Longest message written to the wire.</summary>
    public const int MaximumMessageLength = 300;

    /// <summary>
    ///     Field names whose contents are prose by definition, matched on the leaf path segment.
    /// </summary>
    /// <remarks>
    ///     <c>Notes</c> is the one conventions names; the rest are the spellings the legacy schema and
    ///     the migration design use for the same kind of column. Matching is case-insensitive because
    ///     the path arrives CLR-cased from one handler and camel-cased from the other.
    /// </remarks>
    private static readonly HashSet<string> FreeTextFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "Notes",
        "Note",
        "Comment",
        "Comments",
        "Description",
        "Detail",
        "Details",
        "Reason",
        "Remarks"
    };

    public static string Sanitise(string? message, string? clrPath, object? attemptedValue)
    {
        if (string.IsNullOrWhiteSpace(message))
            return Redacted;

        if (IsFreeTextField(clrPath))
            return Redacted;

        string sanitised = message;

        // Only strings are redacted. A number or a Guid rendered by {PropertyValue} is bounded by
        // construction, and blanking one would remove the only detail that makes the message useful.
        if (attemptedValue is string value
            && value.Length > MaximumEchoedValueLength
            && sanitised.Contains(value, StringComparison.Ordinal))
        {
            sanitised = sanitised.Replace(value, RedactedValue, StringComparison.Ordinal);
        }

        return sanitised.Length <= MaximumMessageLength
            ? sanitised
            : sanitised[..MaximumMessageLength];
    }

    private static bool IsFreeTextField(string? clrPath)
    {
        if (string.IsNullOrEmpty(clrPath))
            return false;

        int dot = clrPath.LastIndexOf('.');
        string leaf = dot < 0 ? clrPath : clrPath[(dot + 1)..];

        int bracket = leaf.IndexOf('[', StringComparison.Ordinal);

        if (bracket >= 0)
            leaf = leaf[..bracket];

        return FreeTextFields.Contains(leaf);
    }
}
