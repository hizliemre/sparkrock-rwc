namespace domain.Exceptions;

/// <summary>
///     Codes for the <c>SCHOOL</c> area.
/// </summary>
/// <remarks>
///     A file rather than a line in a shared one (conventions §5), so twelve workstreams adding codes
///     never meet in the same place.
///     <para>
///         Cross-tenant and genuinely absent both raise <see cref="NotFoundException" /> with
///         <see cref="School.NotFound" /> and no message, which is what makes the two payloads
///         identical by construction rather than by call-site discipline.
///         <see cref="School.Inactive" /> is used by the save path (F02 §7 assigns it there) and is
///         declared here rather than in the attendance area, because the school is the resource the
///         status is decided by.
///     </para>
/// </remarks>
public static partial class ErrorCodes
{
    public static class School
    {
        public const string NotFound = "SCHOOL.NOT_FOUND";

        /// <summary>
        ///     The addressed school is deactivated, so it cannot be written to.
        /// </summary>
        /// <remarks>
        ///     A <b>409</b>, not a 400: the school is the addressed resource, and conventions §2 decides
        ///     status by the addressed resource rather than by an accumulated item. The other half of
        ///     V-14 — an inactive attendance <em>code</em> — is a 400 field error, because a code
        ///     arrives in the body.
        ///     <para>
        ///         Reading an inactive school is still a 200 (DEC-19). This refuses the write only.
        ///     </para>
        /// </remarks>
        public const string Inactive = "SCHOOL.INACTIVE";
    }
}
