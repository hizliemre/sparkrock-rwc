using System.Text;
using features.Paging;

namespace features.tests.AttendanceSubmissions;

/// <summary>
///     O-06's wire half: the opaque <c>(SubmittedAt, Id)</c> cursor.
/// </summary>
/// <remarks>
///     <b>Unit tier.</b> Pure string work — no provider, no fixture, no clock — so nothing here needs
///     a database and nothing here proves anything about one. The relational half of O-06 (a page
///     boundary where two rows share <c>submitted_at</c> to the microsecond) is
///     <c>features.integration.tests</c>' and is deliberately not restated at this tier.
/// </remarks>
public sealed class SubmissionCursorTests
{
    /// <summary>A time with a non-zero microsecond component, which is what <c>timestamptz</c> stores.</summary>
    private static readonly DateTimeOffset MicrosecondPrecise =
        new DateTimeOffset(2026, 9, 14, 8, 31, 0, TimeSpan.Zero).AddTicks(1234567);

    private static readonly Guid Id = Guid.Parse("3f0a1b2c-3d4e-5f60-7182-93a4b5c6d7e8");

    /// <summary>
    ///     The property the whole design rests on: what goes in comes back out, microseconds included.
    /// </summary>
    /// <remarks>
    ///     Microseconds specifically. A format string that truncates to milliseconds passes every
    ///     structural test here and then lands the cursor on the wrong side of a tie against a real
    ///     <c>timestamptz</c> — skipping a row or repeating one, silently.
    /// </remarks>
    [Fact]
    public void Encode_ThenTryDecode_RoundTrips()
    {
        SubmissionCursor original = new(MicrosecondPrecise, Id);

        Assert.True(SubmissionCursor.TryDecode(original.Encode(), out SubmissionCursor decoded));

        Assert.Equal(original.SubmittedAt, decoded.SubmittedAt);
        Assert.Equal(original.Id, decoded.Id);

        // Not just equal to the second: the sub-second component survived in full.
        Assert.Equal(MicrosecondPrecise.Ticks, decoded.SubmittedAt.Ticks);
    }

    /// <summary>A cursor travels in a query string, so the three unsafe Base64 characters must not appear.</summary>
    [Fact]
    public void Encode_ProducesUrlSafeCharacters()
    {
        // Guids and timestamps that exercise the +/ alphabet rather than one arbitrary value: the
        // characters only appear for particular byte triples, so a single sample can pass by luck.
        for (int attempt = 0; attempt < 200; attempt++)
        {
            string encoded = new SubmissionCursor(
                MicrosecondPrecise.AddTicks(attempt), Guid.NewGuid()).Encode();

            Assert.DoesNotContain('+', encoded);
            Assert.DoesNotContain('/', encoded);
            Assert.DoesNotContain('=', encoded);
        }
    }

    /// <summary>
    ///     The same instant in two offsets is the same cursor, so a client cannot page differently by
    ///     sending a cursor it round-tripped through a local-time type.
    /// </summary>
    [Fact]
    public void Encode_NormalisesToUtc()
    {
        DateTimeOffset local = MicrosecondPrecise.ToOffset(TimeSpan.FromHours(3));

        Assert.Equal(new SubmissionCursor(MicrosecondPrecise, Id).Encode(), new SubmissionCursor(local, Id).Encode());
    }

    public static TheoryData<string?> Malformed() => new()
    {
        null,
        "",
        "   ",
        "not-base64!",
        Base64Url("garbage"),
        Base64Url("v1|"),
        Base64Url("v1|notadate|notaguid"),
        Base64Url("v1|2026-09-14T08:31:00.0000000Z|notaguid"),
        Base64Url("v1|notadate|3f0a1b2c-3d4e-5f60-7182-93a4b5c6d7e8"),

        // Wrong version. The prefix exists so the format can change without a client silently
        // mis-parsing an old cursor, which only works if the wrong version is rejected.
        Base64Url("v2|2026-09-14T08:31:00.0000000Z|3f0a1b2c-3d4e-5f60-7182-93a4b5c6d7e8"),

        // Separator missing: two fields concatenated. Reads as one field, which must not be salvaged.
        Base64Url("v12026-09-14T08:31:00.0000000Z3f0a1b2c-3d4e-5f60-7182-93a4b5c6d7e8"),

        // A fourth field. An extra separator is not a version this build understands either.
        Base64Url("v1|2026-09-14T08:31:00.0000000Z|3f0a1b2c-3d4e-5f60-7182-93a4b5c6d7e8|extra"),

        // Valid Base64 in the standard alphabet but not the URL-safe one. Accepting it would mean the
        // decoder is more permissive than the encoder, which is how a "+" that a query string turned
        // into a space becomes a silently different cursor.
        Convert.ToBase64String(Encoding.UTF8.GetBytes(
            "v1|2026-09-14T08:31:00.0000000Z|3f0a1b2c-3d4e-5f60-7182-93a4b5c6d7e8"))
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void TryDecode_WhenMalformed_ReturnsFalse(string? value)
    {
        Assert.False(SubmissionCursor.TryDecode(value, out _));
    }

    /// <summary>
    ///     The hazard <c>SchoolYear.TryParse</c> documents: a caller that ignores the return value must
    ///     not be left holding a half-populated value that looks usable.
    /// </summary>
    [Theory]
    [MemberData(nameof(Malformed))]
    public void TryDecode_WhenFailing_LeavesTheOutParameterDefault(string? value)
    {
        SubmissionCursor.TryDecode(value, out SubmissionCursor cursor);

        Assert.Equal(default, cursor);
        Assert.Equal(Guid.Empty, cursor.Id);
        Assert.Equal(default, cursor.SubmittedAt);
    }

    /// <summary>
    ///     A guard on the theory above: at least one of those payloads must be well-formed Base64Url,
    ///     or every row is rejected by the decoder's first step and the parsing branches below it are
    ///     never reached by any test.
    /// </summary>
    [Fact]
    public void TryDecode_WellFormedBase64UrlOfAWrongPayloadIsStillRejected()
    {
        string encoded = Base64Url("v1|2026-09-14T08:31:00.0000000Z|not-a-guid");

        // The precondition: this really is decodable Base64Url, so the rejection came from the
        // payload and not from the alphabet check.
        Assert.All(encoded, character =>
            Assert.True(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));

        Assert.False(SubmissionCursor.TryDecode(encoded, out _));
    }

    /// <summary>
    ///     Opaque, but not signed and not encrypted (spec §2). Stated as a test so that "opaque" is
    ///     never read as "confidential" by someone who then puts something sensitive in it.
    /// </summary>
    [Fact]
    public void Encode_IsReversibleByAnyone()
    {
        string encoded = new SubmissionCursor(MicrosecondPrecise, Id).Encode();

        string padded = encoded.Replace('-', '+').Replace('_', '/')
            .PadRight(encoded.Length + ((4 - (encoded.Length % 4)) % 4), '=');

        Assert.StartsWith("v1|", Encoding.UTF8.GetString(Convert.FromBase64String(padded)), StringComparison.Ordinal);
    }

    private static string Base64Url(string payload) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
