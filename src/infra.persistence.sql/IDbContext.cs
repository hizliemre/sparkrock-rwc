using domain;
using domain.Alerts;
using domain.Attendance;
using domain.AttendanceCodes;
using domain.Schools;
using domain.SchoolTerms;
using domain.Students;
using Microsoft.EntityFrameworkCore;

namespace infra.persistence.sql;

public interface IDbContext
{
    DbSet<School> Schools { get; set; }

    DbSet<Student> Students { get; set; }

    DbSet<AttendanceCode> AttendanceCodes { get; set; }

    DbSet<SchoolTerm> SchoolTerms { get; set; }

    DbSet<StudentAttendance> StudentAttendances { get; set; }

    DbSet<StudentAttendanceSummary> StudentAttendanceSummaries { get; set; }

    DbSet<StudentAlert> StudentAlerts { get; set; }

    DbSet<AttendanceSubmissionLog> AttendanceSubmissionLogs { get; set; }

    DbSet<TestEntity> TestEntities { get; set; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}