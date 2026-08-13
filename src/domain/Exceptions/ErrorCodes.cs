namespace domain.Exceptions;

/// <summary>
///     Machine-readable failure codes, in the form <c>AREA.CONDITION</c>.
/// </summary>
/// <remarks>
///     Partitioned one nested class per area, one file per area, so a feature adds a file rather than a line to
///     a point twelve workstreams all edit. Identifiers are PascalCase; the wire values are the dotted uppercase
///     form clients branch on.
/// </remarks>
public static partial class ErrorCodes
{
    public static class Validation
    {
        public const string RequiredField = "VALIDATION.REQUIRED_FIELD";
    }
}
