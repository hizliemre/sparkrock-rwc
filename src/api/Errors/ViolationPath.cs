using System.Text;

namespace api.Errors;

/// <summary>
///     Converts a CLR property path into the camel-cased form the wire contract uses.
/// </summary>
/// <remarks>
///     Handlers emit CLR-cased paths such as <c>Entries[3].AttendCode</c>; clients receive
///     <c>entries[3].attendCode</c>, matching the payload they sent.
///     <para>
///         This cannot be delegated to a JSON naming policy. That policy lowercases only the first
///         character of a whole key and never touches string <em>values</em> — and once the path is a
///         string inside a violation object, it is a value. So the transform runs here, once, where
///         the violation is built.
///     </para>
/// </remarks>
internal static class ViolationPath
{
    public static string ToCamelCase(string clrPath)
    {
        if (string.IsNullOrEmpty(clrPath))
            return clrPath;

        StringBuilder result = new(clrPath.Length);
        bool atSegmentStart = true;

        foreach (char character in clrPath)
        {
            if (character is '.')
            {
                atSegmentStart = true;
                result.Append(character);
                continue;
            }

            // An indexer closes a segment; the next character after it is not a new segment start,
            // so "Entries[3].AttendCode" lowers E and A but leaves the 3 alone.
            if (character is '[' or ']')
            {
                atSegmentStart = false;
                result.Append(character);
                continue;
            }

            result.Append(atSegmentStart ? char.ToLowerInvariant(character) : character);
            atSegmentStart = false;
        }

        return result.ToString();
    }
}
