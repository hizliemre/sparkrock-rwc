namespace features.Attendance;

/// <summary>
///     Configuration for the attendance save path, bound from <c>Attendance:Save</c>.
/// </summary>
/// <remarks>
///     In <c>features</c> rather than <c>domain</c> because it is configuration and <c>domain</c> takes
///     none — the fixed numbers live in <c>domain/Attendance/AttendanceSave.cs</c>.
/// </remarks>
public sealed class AttendanceSaveOptions
{
    /// <summary>The configuration section this binds from.</summary>
    public const string SectionName = "Attendance:Save";

    /// <summary>
    ///     How many days before school-local today a submission may be back-dated.
    /// </summary>
    /// <remarks>
    ///     DEC-12 requires a bound and calls it "a configured back-dating window" without naming a
    ///     number; V-25 ● is unsigned. <b>30 is an engineering default with no business input</b>
    ///     (plan R-6), and the direction that matters is the permissive one: back-dating is the quiet
    ///     path to auto-resolving a safeguarding alert, because a correction that drops a student's
    ///     total below the threshold closes an open episode with no human involved (DEC-18).
    ///     <para>
    ///         It is one configuration key and one handler boundary, so changing it is cheap — which
    ///         is the point of it being configuration rather than a constant.
    ///     </para>
    /// </remarks>
    public int BackDatingWindowDays { get; set; } = 30;
}
