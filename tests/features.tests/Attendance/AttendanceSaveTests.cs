using domain.Attendance;
using features.Attendance;

namespace features.tests.Attendance;

/// <summary>
///     The named constants F07 owns (T07-02), and the one configuration value it binds.
/// </summary>
/// <remarks>
///     O-42 records that the non-functional numbers in this system were prose with no constant behind
///     them, and <c>PagingRules</c>' own doc comment says "the submission batch cap of 500 stays
///     unsourced and is F07's to name". These assertions are what make the numbers a contract rather
///     than a habit: DEC-14 pins the attempt bound at initial-plus-two, and a fourth attempt is a
///     decision, not a tuning knob.
/// </remarks>
public sealed class AttendanceSaveTests
{
    [Fact]
    public void MaxAttempts_IsThree()
    {
        Assert.Equal(3, AttendanceSave.MaxAttempts);
    }

    [Fact]
    public void MaxBatchSize_IsFiveHundred()
    {
        Assert.Equal(500, AttendanceSave.MaxBatchSize);
    }

    /// <summary>Legacy <c>Notes VARCHAR(500)</c> and <c>AttendCode VARCHAR(5)</c> (DEC-06).</summary>
    [Fact]
    public void LengthBounds_MirrorTheColumns()
    {
        Assert.Equal(500, AttendanceSave.MaxNotesLength);
        Assert.Equal(5, AttendanceSave.MaxAttendCodeLength);
        Assert.Equal(64, AttendanceSave.MaxIdempotencyKeyLength);
    }

    /// <summary>
    ///     R-6: 30 is an engineering default with no business input, so it is asserted rather than
    ///     assumed — changing it is a visible edit.
    /// </summary>
    [Fact]
    public void BackDatingWindowDays_DefaultsToThirty()
    {
        Assert.Equal(30, new AttendanceSaveOptions().BackDatingWindowDays);
    }
}
