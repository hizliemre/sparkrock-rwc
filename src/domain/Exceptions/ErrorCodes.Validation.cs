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
    }
}
