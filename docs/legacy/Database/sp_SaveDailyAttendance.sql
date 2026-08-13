CREATE PROCEDURE sp_SaveDailyAttendance
    @SchoolID      INT,
    @AttendDate    DATE,
    @AttendanceXML NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON
    DECLARE @StudentID     INT
    DECLARE @AttendCode    VARCHAR(5)
    DECLARE @MinutesLate   INT
    DECLARE @Notes         VARCHAR(500)
    DECLARE @IsAbsent      BIT
    DECLARE @IsExcused     BIT
    DECLARE @ExistingID    INT
    DECLARE @TermID        INT
    DECLARE @TotalAbsences INT
    DECLARE @SchoolYear    VARCHAR(9)
    DECLARE @tbl TABLE (
        StudentID   INT,
        AttendCode  VARCHAR(5),
        MinutesLate INT,
        Notes       VARCHAR(500)
    )
    INSERT INTO @tbl (StudentID, AttendCode, MinutesLate, Notes)
    SELECT
        r.value('@sid',  'INT'),
        r.value('@code', 'VARCHAR(5)'),
        r.value('@min',  'INT'),
        r.value('@note', 'VARCHAR(500)')
    FROM (SELECT CAST(@AttendanceXML AS XML)) x(doc)
    CROSS APPLY x.doc.nodes('/attendance/r') t(r)
    IF MONTH(@AttendDate) >= 9
        SET @SchoolYear = CAST(YEAR(@AttendDate) AS VARCHAR) + '-' + CAST(YEAR(@AttendDate) + 1 AS VARCHAR)
    ELSE
        SET @SchoolYear = CAST(YEAR(@AttendDate) - 1 AS VARCHAR) + '-' + CAST(YEAR(@AttendDate) AS VARCHAR)
    SELECT @TermID = TermID
    FROM   SchoolTerms
    WHERE  SchoolID = @SchoolID
      AND  @AttendDate BETWEEN StartDate AND EndDate
    DECLARE cur CURSOR LOCAL FAST_FORWARD FOR
        SELECT StudentID, AttendCode, MinutesLate, Notes FROM @tbl
    OPEN cur
    FETCH NEXT FROM cur INTO @StudentID, @AttendCode, @MinutesLate, @Notes
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SELECT @IsAbsent = IsAbsent, @IsExcused = IsExcused
        FROM   AttendanceCodes
        WHERE  CodeValue = @AttendCode
        IF @IsAbsent IS NULL
        BEGIN
            SET @IsAbsent = 0
            SET @IsExcused = 0
        END
        SELECT @ExistingID = AttendanceID
        FROM   StudentAttendance
        WHERE  StudentID = @StudentID AND AttendDate = @AttendDate
        IF @ExistingID IS NOT NULL
        BEGIN
            UPDATE StudentAttendance SET
                AttendCode   = @AttendCode,
                IsAbsent     = @IsAbsent,
                IsExcused    = @IsExcused,
                MinutesLate  = @MinutesLate,
                Notes        = @Notes,
                ModifiedDate = GETDATE(),
                ModifiedBy   = SYSTEM_USER
            WHERE AttendanceID = @ExistingID
        END
        ELSE
        BEGIN
            INSERT INTO StudentAttendance
                (StudentID, SchoolID, AttendDate, TermID, AttendCode,
                 IsAbsent, IsExcused, MinutesLate, Notes, CreatedDate, CreatedBy)
            VALUES
                (@StudentID, @SchoolID, @AttendDate, @TermID, @AttendCode,
                 @IsAbsent, @IsExcused, @MinutesLate, @Notes, GETDATE(), SYSTEM_USER)
        END
        -- Recount absences for summary
        SELECT @TotalAbsences = COUNT(*)
        FROM   StudentAttendance
        WHERE  StudentID  = @StudentID
          AND  SchoolID   = @SchoolID
          AND  SchoolYear(@AttendDate) = @SchoolYear
          AND  IsAbsent   = 1
        IF EXISTS (SELECT 1 FROM StudentAttendanceSummary WHERE StudentID = @StudentID AND SchoolYear = @SchoolYear)
        BEGIN
            UPDATE StudentAttendanceSummary SET
                TotalAbsences = @TotalAbsences,
                LastUpdated   = GETDATE()
            WHERE StudentID = @StudentID AND SchoolYear = @SchoolYear
        END
        ELSE
        BEGIN
            INSERT INTO StudentAttendanceSummary (StudentID, SchoolID, SchoolYear, TotalAbsences, LastUpdated)
            VALUES (@StudentID, @SchoolID, @SchoolYear, @TotalAbsences, GETDATE())
        END
        DECLARE @Threshold INT
        SELECT  @Threshold = ISNULL(AbsenceAlertThreshold, 10) FROM Schools WHERE SchoolID = @SchoolID
        IF @TotalAbsences >= @Threshold
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM StudentAlerts
                WHERE  StudentID  = @StudentID
                  AND  AlertType  = 'CHRONIC_ABSENCE'
                  AND  SchoolYear = @SchoolYear
                  AND  ResolvedDate IS NULL
            )
            BEGIN
                INSERT INTO StudentAlerts (StudentID, SchoolID, AlertType, SchoolYear, AlertDate, AlertMessage)
                VALUES (
                    @StudentID, @SchoolID, 'CHRONIC_ABSENCE', @SchoolYear, GETDATE(),
                    'Student has reached ' + CAST(@TotalAbsences AS VARCHAR) + ' absences this year'
                )
            END
        END
        FETCH NEXT FROM cur INTO @StudentID, @AttendCode, @MinutesLate, @Notes
    END
    CLOSE cur
    DEALLOCATE cur
    INSERT INTO AttendanceSubmissionLog (SchoolID, SubmittedDate, AttendDate, RecordCount, SubmittedBy)
    VALUES (@SchoolID, GETDATE(), @AttendDate, (SELECT COUNT(*) FROM @tbl), SYSTEM_USER)
END
