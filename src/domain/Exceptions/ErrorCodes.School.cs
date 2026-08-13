namespace domain.Exceptions;

/// <summary>
///     Codes for the <c>SCHOOL</c> area.
/// </summary>
/// <remarks>
///     A file rather than a line in a shared one (conventions §5), so twelve workstreams adding codes
///     never meet in the same place.
///     <para>
///         One code only. Cross-tenant and genuinely absent both raise
///         <see cref="NotFoundException" /> with this code and no message, which is what makes the two
///         payloads identical by construction rather than by call-site discipline.
///         <c>SCHOOL.INACTIVE</c> belongs to the save path and is not declared here.
///     </para>
/// </remarks>
public static partial class ErrorCodes
{
    public static class School
    {
        public const string NotFound = "SCHOOL.NOT_FOUND";
    }
}
