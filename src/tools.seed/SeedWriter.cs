using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using domain.Abstraction;
using domain.AttendanceCodes;
using domain.Schools;
using domain.SchoolTerms;
using domain.Security;
using domain.Students;
using infra.persistence.sql;
using Microsoft.EntityFrameworkCore;

namespace tools.seed;

/// <summary>
///     Applies a <see cref="SeedPlan" /> through <see cref="IDbContext" />, as an upsert by primary
///     key, in one <c>SaveChangesAsync</c>.
/// </summary>
/// <remarks>
///     <b>Writes through the port, not around it.</b> <c>SparkrockRwcDbContext</c> is
///     <c>internal sealed</c> and VC-33 records that a console tool cannot reach it; F00 does not add
///     an <c>InternalsVisibleTo</c> entry, because it does not need one. <c>WithPostgre()</c>
///     registers the public <see cref="IDbContext" />, which exposes exactly the <c>DbSet</c>s and
///     the single <c>SaveChangesAsync</c> this needs. F12 additionally needs
///     <c>Database.BeginTransactionAsync</c> (DEC-14) and therefore still faces VC-33; F00 must not
///     be cited as having solved it.
///     <para>
///         Going through the port also keeps the seed inside every mechanism the application has —
///         the audit interceptor, the delete guard, the naming convention, the constraint-error
///         translation — rather than beside them. A seed written through raw SQL would be the one
///         code path where a schema mistake does not surface.
///     </para>
///     <para>
///         <b>Nothing is ever removed.</b> <c>Remove</c> on a <see cref="BaseEntity" /> throws in the
///         interceptor (DEC-20), and that is correct here: a seed that deleted rows would delete a
///         developer's hand-made test data along with its own.
///     </para>
/// </remarks>
public sealed class SeedWriter(IDbContext dbContext, IAuditOverride auditOverride)
{
    private enum RowOutcome
    {
        Created,
        Updated,
        Unchanged
    }

    /// <summary>
    ///     Writes the plan and reports what changed.
    /// </summary>
    /// <remarks>
    ///     The whole run is wrapped in <c>IAuditOverride.Begin(SystemImportUser.Id)</c>, so
    ///     <c>created_by</c> / <c>modified_by</c> carry the reserved import identity
    ///     (<c>…00FF</c>) and seed rows stay separable from rows written through the anonymous stub
    ///     (<c>…000A</c>). The override is opened <em>here</em> rather than in <c>Program</c> on
    ///     purpose: attribution that depends on a composition root remembering to open a scope is
    ///     attribution that will one day be wrong, and a test can then prove the branch runs by
    ///     constructing the writer with some other <c>ICurrentUser</c> and asserting the import id
    ///     lands anyway.
    ///     <para>
    ///         One <c>SaveChangesAsync</c> for the whole plan. EF orders the inserts so the school
    ///         precedes its students and terms.
    ///     </para>
    /// </remarks>
    public async Task<SeedResult> WriteAsync(SeedPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        using IDisposable scope = auditOverride.Begin(SystemImportUser.Id);

        List<string> conflicts = [];

        SeedOutcome school = await ApplySchoolAsync(plan.School, cancellationToken).ConfigureAwait(false);
        SeedOutcome codes = await ApplyAttendanceCodesAsync(plan.AttendanceCodes, conflicts, cancellationToken)
            .ConfigureAwait(false);
        SeedOutcome terms = await ApplyTermsAsync(plan.Terms, cancellationToken).ConfigureAwait(false);
        SeedOutcome students = await ApplyStudentsAsync(plan.Students, cancellationToken).ConfigureAwait(false);

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new SeedResult
        {
            Outcomes = [school, codes, terms, students],
            Conflicts = conflicts
        };
    }

    private async Task<SeedOutcome> ApplySchoolAsync(School desired, CancellationToken cancellationToken)
    {
        RowOutcome outcome = await UpsertAsync(dbContext.Schools, desired, CopySchool, cancellationToken)
            .ConfigureAwait(false);

        return Tally("School", [outcome], skipped: 0);
    }

    /// <summary>
    ///     The one entity whose upsert is not purely by primary key — see O-30.
    /// </summary>
    /// <remarks>
    ///     <c>ix_attendance_codes_value</c> is unique and deliberately <b>unfiltered</b>, so
    ///     deactivating a code never frees its value for reuse. A row holding one of the seeded five
    ///     values under a different <c>Id</c> therefore blocks the seed absolutely: inserting is a
    ///     <c>23505</c> that aborts the entire run and leaves nothing seeded, and assigning the
    ///     seeded id's fields onto that row would silently rewrite something the seed does not own.
    ///     <para>
    ///         So the writer matches on <c>Id</c> first and <c>Value</c> second, and on a
    ///         <c>Value</c> match under a foreign id it <b>skips the row and reports it</b>. That is
    ///         the same match order F12's importer is contracted to use (<c>LegacyId</c> first,
    ///         <c>UPPER(Value)</c> second) — the difference is what happens next: F12 <em>adopts</em>
    ///         the row by writing <c>LegacyId</c> onto it, because it has a legacy row to reconcile;
    ///         F00 has nothing to reconcile and so does nothing at all.
    ///     </para>
    ///     <para>
    ///         <b><c>LegacyId</c> is never written by this writer, for any entity.</b> If F12 has
    ///         already adopted a seeded row, a later re-run of the seed must not null the
    ///         <c>LegacyId</c> back out — that would un-adopt the row and make the next import insert
    ///         a duplicate. The seed owns the descriptive columns; it does not own the legacy link.
    ///     </para>
    /// </remarks>
    private async Task<SeedOutcome> ApplyAttendanceCodesAsync(
        IReadOnlyList<AttendanceCode> desired,
        List<string> conflicts,
        CancellationToken cancellationToken)
    {
        string[] values = desired.Select(code => code.Value).ToArray();

        List<AttendanceCode> holders = await dbContext.AttendanceCodes
            .Where(code => values.Contains(code.Value))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<RowOutcome> outcomes = [];
        int skipped = 0;

        foreach (AttendanceCode code in desired)
        {
            AttendanceCode? holder = holders.Find(candidate => candidate.Value == code.Value);

            if (holder is not null && holder.Id != code.Id)
            {
                skipped++;
                conflicts.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"AttendanceCode '{code.Value}' is already held by row {holder.Id}, not by the seeded row "
                    + $"{code.Id}. The seed left it untouched: ix_attendance_codes_value is unique and unfiltered, "
                    + $"so inserting would abort the run and updating would overwrite a row the seed does not own."));
                continue;
            }

            outcomes.Add(await UpsertAsync(dbContext.AttendanceCodes, code, CopyAttendanceCode, cancellationToken)
                .ConfigureAwait(false));
        }

        return Tally("AttendanceCode", outcomes, skipped);
    }

    private async Task<SeedOutcome> ApplyTermsAsync(
        IReadOnlyList<SchoolTerm> desired,
        CancellationToken cancellationToken)
    {
        List<RowOutcome> outcomes = [];

        foreach (SchoolTerm term in desired)
            outcomes.Add(await UpsertAsync(dbContext.SchoolTerms, term, CopyTerm, cancellationToken)
                .ConfigureAwait(false));

        return Tally("SchoolTerm", outcomes, skipped: 0);
    }

    private async Task<SeedOutcome> ApplyStudentsAsync(
        IReadOnlyList<Student> desired,
        CancellationToken cancellationToken)
    {
        List<RowOutcome> outcomes = [];

        foreach (Student student in desired)
            outcomes.Add(await UpsertAsync(dbContext.Students, student, CopyStudent, cancellationToken)
                .ConfigureAwait(false));

        return Tally("Student", outcomes, skipped: 0);
    }

    /// <summary>
    ///     Insert if the key is absent, otherwise copy the mutable fields onto the persisted row.
    /// </summary>
    /// <remarks>
    ///     <c>copyMutableFields</c> returns whether it changed anything, and <b>this is decided by
    ///     comparing values, not by asking the change tracker</b>. EF no-ops an assignment of an
    ///     equal value, so a writer that assigned unconditionally would still report "unchanged" if
    ///     the tracker were the oracle — and a test built on that oracle passes whatever the writer
    ///     does. Comparing first also means no <c>UPDATE</c> statement is issued for an unchanged
    ///     row, which is what keeps <c>modified_at</c> null across a second run and gives the
    ///     idempotency test something the writer cannot fake.
    /// </remarks>
    private static async Task<RowOutcome> UpsertAsync<TEntity>(
        DbSet<TEntity> set,
        TEntity desired,
        Func<TEntity, TEntity, bool> copyMutableFields,
        CancellationToken cancellationToken)
        where TEntity : BaseEntity
    {
        TEntity? existing = await set.FindAsync([desired.Id], cancellationToken).ConfigureAwait(false);

        if (existing is null)
        {
            set.Add(desired);
            return RowOutcome.Created;
        }

        return copyMutableFields(existing, desired) ? RowOutcome.Updated : RowOutcome.Unchanged;
    }

    private static bool CopySchool(School target, School source)
    {
        if (target.Name == source.Name
            && target.TimeZoneId == source.TimeZoneId
            && target.AbsenceAlertThreshold == source.AbsenceAlertThreshold
            && target.IsActive == source.IsActive)
            return false;

        target.Name = source.Name;
        target.TimeZoneId = source.TimeZoneId;
        target.AbsenceAlertThreshold = source.AbsenceAlertThreshold;
        target.IsActive = source.IsActive;

        return true;
    }

    private static bool CopyAttendanceCode(AttendanceCode target, AttendanceCode source)
    {
        if (target.Value == source.Value
            && target.Description == source.Description
            && target.IsAbsent == source.IsAbsent
            && target.IsExcused == source.IsExcused
            && target.IsActive == source.IsActive)
            return false;

        target.Value = source.Value;
        target.Description = source.Description;
        target.IsAbsent = source.IsAbsent;
        target.IsExcused = source.IsExcused;
        target.IsActive = source.IsActive;

        return true;
    }

    private static bool CopyTerm(SchoolTerm target, SchoolTerm source)
    {
        if (target.SchoolId == source.SchoolId
            && target.Name == source.Name
            && target.StartDate == source.StartDate
            && target.EndDate == source.EndDate
            && target.IsActive == source.IsActive)
            return false;

        target.SchoolId = source.SchoolId;
        target.Name = source.Name;
        target.StartDate = source.StartDate;
        target.EndDate = source.EndDate;
        target.IsActive = source.IsActive;

        return true;
    }

    private static bool CopyStudent(Student target, Student source)
    {
        if (target.SchoolId == source.SchoolId
            && target.FirstName == source.FirstName
            && target.LastName == source.LastName
            && target.Grade == source.Grade
            && target.IsActive == source.IsActive)
            return false;

        target.SchoolId = source.SchoolId;
        target.FirstName = source.FirstName;
        target.LastName = source.LastName;
        target.Grade = source.Grade;
        target.IsActive = source.IsActive;

        return true;
    }

    private static SeedOutcome Tally(string entity, IReadOnlyList<RowOutcome> outcomes, int skipped) =>
        new()
        {
            Entity = entity,
            Created = outcomes.Count(outcome => outcome == RowOutcome.Created),
            Updated = outcomes.Count(outcome => outcome == RowOutcome.Updated),
            Unchanged = outcomes.Count(outcome => outcome == RowOutcome.Unchanged),
            Skipped = skipped
        };
}
