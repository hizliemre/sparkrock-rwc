namespace domain.Exceptions;

/// <summary>
///     Machine-readable failure codes, in the form <c>AREA.CONDITION</c>.
/// </summary>
/// <remarks>
///     One nested class per area, one file per area, so a feature adds a file rather than a line to a
///     point twelve workstreams all edit.
///     <para>
///         Identifiers are PascalCase and the wire values are upper snake case. The values are the
///         contract clients branch on; upper-snake identifiers would read closer to the value but
///         trip CA1707, and the analyzer is enforcing a real convention here.
///     </para>
/// </remarks>
public static partial class ErrorCodes
{
    public static class Validation
    {
        /// <summary>Envelope-level code for any failure produced by a validator.</summary>
        public const string Failed = "VALIDATION.FAILED";

        public const string RequiredField = "VALIDATION.REQUIRED_FIELD";

        public const string PageSizeExceeded = "VALIDATION.PAGE_SIZE_EXCEEDED";

        /// <summary>
        ///     The <c>?cursor=</c> on a keyset-paged route is malformed, not Base64Url, carries a
        ///     version this build does not understand, or decodes to something that is not a
        ///     <c>(submittedAt, id)</c> pair.
        /// </summary>
        /// <remarks>
        ///     <b>Never silently ignored.</b> Ignoring an undecodable cursor serves page 1, so a client
        ///     paging in a loop follows <c>nextCursor</c> back to the beginning and never terminates —
        ///     a hang rather than an error, which is the harder failure to diagnose.
        ///     <para>
        ///         In <c>VALIDATION</c> rather than in an area class, although conventions §5's
        ///         one-file-per-area rule exists to keep additions out of shared files. A cursor is a
        ///         paging concern: <c>ATTENDANCE.INVALID_CURSOR</c> would make a generic paging failure
        ///         area-specific for the next keyset endpoint. The same call F01a made for
        ///         <see cref="PageSizeExceeded" />.
        ///     </para>
        /// </remarks>
        public const string InvalidCursor = "VALIDATION.INVALID_CURSOR";
    }
}
