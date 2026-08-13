using System.Globalization;
using System.Text;

namespace features.Paging;

/// <summary>
///     O-06's resolution: the opaque, composite page cursor a keyset-paged collection hands back.
/// </summary>
/// <remarks>
///     <b>Why a composite.</b> <c>SubmittedAt</c> is <c>timestamptz</c>, which Postgres stores at
///     microsecond resolution. Two submissions by one school — a clerk correcting yesterday, then
///     today — can land in the same microsecond, and a batch import or a scripted client makes that
///     likely rather than exotic. A cursor on the timestamp alone then either <em>skips</em> rows
///     (with <c>&lt;</c>) or <em>repeats</em> them forever (with <c>&lt;=</c>). The tie-break on
///     <c>Id</c> is what closes that, and
///     <c>KeysetPagingTests.Keyset_WhenTimestampsTieToTheMicrosecond_DoesNotSkipOrRepeat</c> is what
///     proves it against a real column.
///     <para>
///         <b>Opaque for evolvability, not for security.</b> It is not signed, and does not need to
///         be: the cursor carries <em>no authorisation input</em>. The school comes from the route and
///         is re-checked on every request, so the only thing a forged cursor can do is start the
///         caller's own page somewhere else in their own school's log. Saying that explicitly matters,
///         because "opaque token" invites an HMAC that then needs a key, a rotation story and a
///         version story for a threat that does not exist here.
///         <c>SubmissionCursorTests.Encode_IsReversibleByAnyone</c> pins the absence of signing so
///         nobody later reads "opaque" as "confidential" and puts something sensitive in it.
///     </para>
///     <para>
///         <b>The decoded values are only ever passed as parameters.</b> All comparison and all
///         ordering happen in Postgres. This matters more than it looks: .NET's
///         <see cref="Guid.CompareTo(Guid)" /> orders by field (<c>Data1</c> as a signed <c>int</c>,
///         then <c>Data2</c>…) while Postgres orders <c>uuid</c> as sixteen big-endian bytes, and the
///         two <b>disagree</b>. Because nothing here — and nothing in the handler — ever sorts or
///         compares a <see cref="Guid" /> in C#, the disagreement never participates.
///     </para>
///     <para>
///         <b>Where this file lives.</b> The F11 plan puts it in <c>src/features/Paging/</c> beside
///         <c>PagedResponse</c> and <c>PagingRules</c>, and that is where it belongs — it is a paging
///         concern with no attendance meaning. It sits here because the F11 implementation task's edit
///         boundary excluded <c>src/features/Paging/</c>. The namespace is <c>features.Paging</c>
///         regardless, so relocating the file is a move with no code change anywhere.
///     </para>
/// </remarks>
public readonly record struct SubmissionCursor(DateTimeOffset SubmittedAt, Guid Id)
{
    /// <summary>
    ///     The format marker, so the encoding can change without a client silently mis-parsing an old
    ///     cursor.
    /// </summary>
    /// <remarks>
    ///     Load-bearing only because <see cref="TryDecode" /> <em>rejects</em> anything else. A prefix
    ///     that is written and never checked is decoration.
    /// </remarks>
    private const string Version = "v1";

    private const char Separator = '|';

    /// <summary>Round-trip format. Keeps the sub-second component in full; <c>"o"</c> would too.</summary>
    private const string InstantFormat = "O";

    /// <summary>The plain <c>Guid</c> form, <c>00000000-0000-0000-0000-000000000000</c>.</summary>
    private const string IdFormat = "D";

    /// <summary>Base64Url of UTF-8 <c>v1|{submittedAt:O}|{id:D}</c>, normalised to UTC.</summary>
    public string Encode()
    {
        string payload = string.Create(
            CultureInfo.InvariantCulture,
            $"{Version}{Separator}{SubmittedAt.ToUniversalTime().ToString(InstantFormat, CultureInfo.InvariantCulture)}{Separator}{Id.ToString(IdFormat, CultureInfo.InvariantCulture)}");

        return ToBase64Url(Encoding.UTF8.GetBytes(payload));
    }

    /// <summary>
    ///     Decodes a cursor, or reports that it could not.
    /// </summary>
    /// <remarks>
    ///     <c>TryDecode</c> rather than <c>Decode</c>, for the reason <c>SchoolYear.TryParse</c> gives:
    ///     the failure is a 400 a validator produces, not an exception a handler catches. It never
    ///     throws, whatever the input.
    ///     <para>
    ///         <paramref name="cursor" /> is assigned <c>default</c> before anything else, so a caller
    ///         that ignores the return value cannot be left holding a half-populated value that looks
    ///         usable.
    ///     </para>
    /// </remarks>
    public static bool TryDecode(string? value, out SubmissionCursor cursor)
    {
        cursor = default;

        if (string.IsNullOrEmpty(value) || !TryFromBase64Url(value, out byte[] payloadBytes))
            return false;

        // Lenient UTF-8: invalid bytes become U+FFFD rather than throwing, and the parsing below then
        // rejects the result anyway. A strict decoder would only move the same rejection into a catch.
        string[] parts = Encoding.UTF8.GetString(payloadBytes).Split(Separator);

        if (parts.Length != 3 || !string.Equals(parts[0], Version, StringComparison.Ordinal))
            return false;

        // ParseExact, not Parse: the whole point of "O" is that it round-trips, and accepting anything
        // else here would accept a cursor whose sub-second component was silently dropped.
        if (!DateTimeOffset.TryParseExact(
                parts[1],
                InstantFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset submittedAt))
        {
            return false;
        }

        if (!Guid.TryParseExact(parts[2], IdFormat, out Guid id))
            return false;

        cursor = new SubmissionCursor(submittedAt.ToUniversalTime(), id);

        return true;
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>
    ///     The inverse, strict about the alphabet.
    /// </summary>
    /// <remarks>
    ///     The explicit character check is not redundant with
    ///     <see cref="Convert.TryFromBase64String" />: that accepts the standard alphabet and skips
    ///     whitespace, so <c>+</c> and <c>/</c> would decode here while never being produced by
    ///     <see cref="ToBase64Url" />. A decoder more permissive than its encoder is how a <c>+</c>
    ///     that a query string turned into a space becomes a silently different cursor rather than a
    ///     400.
    /// </remarks>
    private static bool TryFromBase64Url(string value, out byte[] bytes)
    {
        bytes = [];

        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
                return false;
        }

        int padding = (4 - (value.Length % 4)) % 4;

        // A remainder of 1 cannot be padded into a valid Base64 quantum. Rejected here rather than
        // left to produce a confusing failure two lines down.
        if (padding == 3)
            return false;

        string standard = string.Concat(value.Replace('-', '+').Replace('_', '/'), new string('=', padding));

        byte[] buffer = new byte[standard.Length / 4 * 3];

        if (!Convert.TryFromBase64String(standard, buffer, out int written))
            return false;

        bytes = buffer[..written];

        return true;
    }
}
