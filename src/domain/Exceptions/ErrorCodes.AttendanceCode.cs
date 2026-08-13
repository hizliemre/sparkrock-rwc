namespace domain.Exceptions;

/// <summary>
///     Codes for the <c>ATTENDANCE_CODE</c> area.
/// </summary>
/// <remarks>
///     A file rather than a line in a shared one (conventions §5), so twelve workstreams adding codes
///     never meet in the same place.
///     <para>
///         F03's spec and tasks both state this file already exists, shipped by F01c carrying
///         <see cref="DuplicateValue" />. It did not: <c>ATTENDANCE_CODE</c> was in conventions §5's
///         closed area set and in <c>ErrorCodesTests.ClosedAreaSet</c>, but no area class declared it,
///         so nothing failed. F03 is the first consumer and therefore authors it.
///     </para>
/// </remarks>
public static partial class ErrorCodes
{
    public static class AttendanceCode
    {
        /// <summary>
        ///     The value is already taken, whether or not the occupant is active.
        /// </summary>
        /// <remarks>
        ///     Raised by <c>ix_attendance_codes_value</c> through the constraint registry, never by a
        ///     pre-<c>SELECT</c> in a handler: the index is the only race-free authority. The index is
        ///     <b>unfiltered</b>, so deactivating a code never frees its value for reuse — a second
        ///     <c>POST</c> of a deactivated code's value is a 409 and the only route back is
        ///     <c>PUT { "isActive": true }</c> on the existing row.
        /// </remarks>
        public const string DuplicateValue = "ATTENDANCE_CODE.DUPLICATE_VALUE";

        /// <summary>
        ///     No attendance code carries the id in the path.
        /// </summary>
        /// <remarks>
        ///     The aggregate is global (conventions §1), so there is no tenancy and this code means
        ///     exactly one thing. A 404 here is never a disguised 403.
        /// </remarks>
        public const string NotFound = "ATTENDANCE_CODE.NOT_FOUND";

        /// <summary>
        ///     A <c>PUT</c> body carried a <c>value</c> that is not the stored one.
        /// </summary>
        /// <remarks>
        ///     A changed value would orphan the text already snapshotted into
        ///     <c>StudentAttendance.AttendCode</c> (D-02, V-23) and would move an occupancy in the
        ///     unfiltered unique namespace. The body carries <c>value</c> anyway because unmatched
        ///     JSON members are ignored by default: omitting it from the request model would answer a
        ///     rename attempt with 200 and no change.
        /// </remarks>
        public const string ValueImmutable = "ATTENDANCE_CODE.VALUE_IMMUTABLE";
    }
}
