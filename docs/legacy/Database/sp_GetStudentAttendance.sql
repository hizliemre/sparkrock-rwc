CREATE PROCEDURE sp_GetStudentAttendance
    @StudentID   INT,
    @SchoolYear  VARCHAR(9) = NULL
AS
BEGIN
    SET NOCOUNT ON
    DECLARE @Year VARCHAR(9)
    IF @SchoolYear IS NULL
    BEGIN
        IF MONTH(GETDATE()) >= 9
            SET @Year = CAST(YEAR(GETDATE()) AS VARCHAR) + '-' + CAST(YEAR(GETDATE()) + 1 AS VARCHAR)
        ELSE
            SET @Year = CAST(YEAR(GETDATE()) - 1 AS VARCHAR) + '-' + CAST(YEAR(GETDATE()) AS VARCHAR)
    END
    ELSE
        SET @Year = @SchoolYear
    SELECT
        sa.AttendDate,
        sa.AttendCode,
        ac.Description  AS AttendCodeDescription,
        sa.IsAbsent,
        sa.IsExcused,
        sa.MinutesLate,
        sa.Notes,
        st.TermName
    FROM  StudentAttendance sa
    INNER JOIN AttendanceCodes ac ON ac.CodeValue = sa.AttendCode
    LEFT  JOIN SchoolTerms st     ON st.TermID    = sa.TermID
    WHERE sa.StudentID = @StudentID
      AND CASE
            WHEN MONTH(sa.AttendDate) >= 9
            THEN CAST(YEAR(sa.AttendDate) AS VARCHAR) + '-' + CAST(YEAR(sa.AttendDate) + 1 AS VARCHAR)
            ELSE CAST(YEAR(sa.AttendDate) - 1 AS VARCHAR) + '-' + CAST(YEAR(sa.AttendDate) AS VARCHAR)
          END = @Year
    ORDER BY sa.AttendDate DESC
    SELECT
        sas.TotalAbsences,
        sas.LastUpdated,
        sc.AbsenceAlertThreshold,
        CASE WHEN sas.TotalAbsences >= ISNULL(sc.AbsenceAlertThreshold, 10) THEN 1 ELSE 0 END AS IsChronicallyAbsent
    FROM  StudentAttendanceSummary sas
    INNER JOIN Students s  ON s.StudentID  = sas.StudentID
    INNER JOIN Schools  sc ON sc.SchoolID  = sas.SchoolID
    WHERE sas.StudentID = @StudentID
      AND sas.SchoolYear = @Year
END
