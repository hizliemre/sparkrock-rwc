using domain;
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

    DbSet<TestEntity> TestEntities { get; set; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}