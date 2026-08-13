namespace domain.Exceptions;

/// <summary>
///     Codes for the <c>ALERT</c> area.
/// </summary>
/// <remarks>
///     A file rather than a line in a shared one (conventions §5), so twelve workstreams adding codes
///     never meet in the same place.
/// </remarks>
public static partial class ErrorCodes
{
    public static class Alert
    {
        /// <summary>
        ///     An open episode already exists for this student, alert type, school year and school.
        /// </summary>
        /// <remarks>
        ///     DEC-18 makes one row one episode, and the partial unique index
        ///     <c>ix_student_alerts_open_episode</c> is what makes a double-raise impossible rather
        ///     than merely unlikely. This code is what the client sees when that index fires and the
        ///     retry did not absorb it.
        /// </remarks>
        public const string DuplicateOpenEpisode = "ALERT.DUPLICATE_OPEN_EPISODE";
    }
}
