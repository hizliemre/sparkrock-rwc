CREATE TABLE Students (
    StudentID    INT          IDENTITY(1,1) PRIMARY KEY,
    SchoolID     INT          NOT NULL,
    FirstName    VARCHAR(100) NOT NULL,
    LastName     VARCHAR(100) NOT NULL,
    Grade        VARCHAR(10)  NULL,
    DateOfBirth  DATE         NULL,
    Active       BIT          NOT NULL DEFAULT 1,
    CreatedDate  DATETIME     NOT NULL DEFAULT GETDATE()
)

CREATE TABLE AttendanceCodes (
    CodeID       INT          IDENTITY(1,1) PRIMARY KEY,
    CodeValue    VARCHAR(5)   NOT NULL UNIQUE,
    Description  VARCHAR(100) NOT NULL,
    IsAbsent     BIT          NOT NULL DEFAULT 0,
    IsExcused    BIT          NOT NULL DEFAULT 0,
    IsActive     BIT          NOT NULL DEFAULT 1
)

CREATE TABLE StudentAttendance (
    AttendanceID  INT          IDENTITY(1,1) PRIMARY KEY,
    StudentID     INT          NOT NULL,
    SchoolID      INT          NOT NULL,
    AttendDate    DATE         NOT NULL,
    TermID        INT          NULL,
    AttendCode    VARCHAR(5)   NOT NULL,
    IsAbsent      BIT          NOT NULL DEFAULT 0,
    IsExcused     BIT          NOT NULL DEFAULT 0,
    MinutesLate   INT          NULL DEFAULT 0,
    Notes         VARCHAR(500) NULL,
    CreatedDate   DATETIME     NOT NULL DEFAULT GETDATE(),
    CreatedBy     VARCHAR(100) NULL,
    ModifiedDate  DATETIME     NULL,
    ModifiedBy    VARCHAR(100) NULL
)

CREATE TABLE StudentAttendanceSummary (
    SummaryID      INT         IDENTITY(1,1) PRIMARY KEY,
    StudentID      INT         NOT NULL,
    SchoolID       INT         NOT NULL,
    SchoolYear     VARCHAR(9)  NOT NULL,
    TotalAbsences  INT         NOT NULL DEFAULT 0,
    LastUpdated    DATETIME    NOT NULL DEFAULT GETDATE(),
    CONSTRAINT UQ_Summary UNIQUE (StudentID, SchoolYear)
)

CREATE TABLE StudentAlerts (
    AlertID       INT          IDENTITY(1,1) PRIMARY KEY,
    StudentID     INT          NOT NULL,
    SchoolID      INT          NOT NULL,
    AlertType     VARCHAR(50)  NOT NULL,
    SchoolYear    VARCHAR(9)   NOT NULL,
    AlertDate     DATETIME     NOT NULL DEFAULT GETDATE(),
    AlertMessage  VARCHAR(500) NULL,
    ResolvedDate  DATETIME     NULL,
    ResolvedBy    VARCHAR(100) NULL
)
