using System.Text;
using System.Text.Json;

namespace api.Errors;

/// <summary>
///     Converts a CLR property path into the camel-cased form the wire contract uses.
/// </summary>
/// <remarks>
///     Handlers emit CLR-cased paths such as <c>Entries[3].AttendCode</c>; clients receive
///     <c>entries[3].attendCode</c>, matching the payload they sent.
///     <para>
///         The transform runs here rather than in the serializer because a JSON naming policy renames
///         <em>keys</em> and never touches string <em>values</em> — and once the path is a string
///         inside a violation object, it is a value.
///     </para>
///     <para>
///         What it must not do is invent its own casing rule. Lowering only the first character gives
///         <c>iDNumber</c> for <c>IDNumber</c>, while the serializer writes the property itself as
///         <c>idNumber</c>: the path then points at a key that does not exist in the payload, which is
///         worse than no path at all. So each segment's identifier is handed to the same
///         <see cref="JsonNamingPolicy.CamelCase" /> instance the response serializer uses, and only
///         the <c>[n]</c> indexer suffix is carried across untouched.
///     </para>
/// </remarks>
internal static class ViolationPath
{
    public static string ToCamelCase(string? clrPath)
    {
        // A null path would serialise as "path": null, which is not a member of the contract.
        if (string.IsNullOrEmpty(clrPath))
            return string.Empty;

        StringBuilder result = new(clrPath.Length);
        bool first = true;

        foreach (string segment in clrPath.Split('.'))
        {
            if (!first)
                result.Append('.');

            first = false;
            AppendSegment(result, segment);
        }

        return result.ToString();
    }

    /// <summary>
    ///     Camel-cases the identifier and copies any <c>[n]</c> suffix verbatim.
    /// </summary>
    /// <remarks>
    ///     Split on the first <c>[</c> rather than passing the whole segment to the policy: the policy
    ///     is defined over identifiers, and an index is not one. <c>Entries[3]</c> keeps its 3.
    /// </remarks>
    private static void AppendSegment(StringBuilder result, string segment)
    {
        int bracket = segment.IndexOf('[', StringComparison.Ordinal);

        if (bracket < 0)
        {
            result.Append(JsonNamingPolicy.CamelCase.ConvertName(segment));
            return;
        }

        result.Append(JsonNamingPolicy.CamelCase.ConvertName(segment[..bracket]));
        result.Append(segment[bracket..]);
    }
}
