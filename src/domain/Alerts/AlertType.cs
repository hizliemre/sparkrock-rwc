namespace domain.Alerts;

/// <summary>
///     The kind of condition an alert episode records.
/// </summary>
/// <remarks>
///     Persisted as a string rather than an ordinal so a reordering cannot silently reinterpret
///     history, and so the column is legible in the database during a cutover.
/// </remarks>
public enum AlertType
{
    /// <summary>Absences reached the school's threshold for the school year.</summary>
    ChronicAbsence = 1
}
