using domain.Attendance;
using domain.AttendanceCodes;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace features.tests.Model;

/// <summary>
///     The D-02 snapshot invariant: a recorded attendance row keeps the meaning the code had on the
///     day it was recorded.
/// </summary>
/// <remarks>
///     <para>
///         Legacy joined <c>StudentAttendance</c> to <c>AttendanceCodes</c> at read time, so editing
///         a description rewrote every historical record that used it — a register printed last year
///         changed retroactively. V-23 diverges from that by copying the code's meaning onto the row
///         at write time.
///     </para>
///     <para>
///         V-23 carries a <b>●</b>: it changes what users see. Someone correcting a typo in a
///         description will no longer see the correction in historical views, which is the intended
///         behaviour and still a surprise. Business acceptance is a cutover gate, not this test's.
///     </para>
///     <para>
///         <c>Model_StudentAttendanceHasNoAttendanceCodeNavigation</c> (T01d-02) is the structural
///         half of the same invariant: with no navigation the read-time join does not compile, so the
///         legacy shape cannot be reintroduced by habit. This file is the behavioural half.
///     </para>
/// </remarks>
public sealed class StudentAttendanceSnapshotTests
{
    private static readonly Guid CodeId = Guid.Parse("2a1c0f1e-0000-4000-8000-000000000001");
    private static readonly Guid AttendanceId = Guid.Parse("2a1c0f1e-0000-4000-8000-000000000002");
    private static readonly Guid StudentId = Guid.Parse("2a1c0f1e-0000-4000-8000-000000000003");
    private static readonly Guid SchoolId = Guid.Parse("2a1c0f1e-0000-4000-8000-000000000004");

    /// <summary>
    ///     Redefining an attendance code leaves already-recorded rows untouched.
    /// </summary>
    /// <remarks>
    ///     The mutation here is not a typo fix — it inverts the code's meaning, turning an unexcused
    ///     absence into an authorised one. Under the legacy read-time join that would have silently
    ///     excused every past absence recorded with this code, including the ones a chronic-absence
    ///     threshold was already computed from.
    /// </remarks>
    [Fact]
    public async Task Snapshot_WhenAttendanceCodeIsRedefined_StoredRowIsUnchanged()
    {
        await using SparkrockRwcDbContext context = InMemoryDbContextFactory.Create();

        context.AttendanceCodes.Add(new AttendanceCode
        {
            Id = CodeId,
            Value = "A",
            Description = "Absent",
            IsAbsent = true,
            IsExcused = false,
        });

        context.StudentAttendances.Add(new StudentAttendance
        {
            Id = AttendanceId,
            StudentId = StudentId,
            SchoolId = SchoolId,
            AttendDate = new DateOnly(2026, 9, 14),
            AttendanceCodeId = CodeId,
            AttendCode = "A",
            AttendCodeDescription = "Absent",
            IsAbsent = true,
            IsExcused = false,
        });

        await context.SaveChangesAsync();

        AttendanceCode code = await context.AttendanceCodes.SingleAsync(c => c.Id == CodeId);
        code.Description = "Authorised absence";
        code.IsAbsent = false;
        code.IsExcused = true;
        await context.SaveChangesAsync();

        // Identity resolution would otherwise hand back the instance already tracked from the insert,
        // which proves nothing about what was stored. Clearing the change tracker forces the read to
        // go to the store, which is the same reset a fresh context would give.
        context.ChangeTracker.Clear();

        StudentAttendance stored = await context.StudentAttendances.SingleAsync(a => a.Id == AttendanceId);

        Assert.Equal("A", stored.AttendCode);
        Assert.Equal("Absent", stored.AttendCodeDescription);
        Assert.True(stored.IsAbsent);
        Assert.False(stored.IsExcused);

        // The foreign key still points at the redefined code. The snapshot is additional to the
        // relationship, not a replacement for it — F12 needs the key to reconcile the import.
        Assert.Equal(CodeId, stored.AttendanceCodeId);
    }

    /// <summary>
    ///     The snapshot columns are stored values, not computed ones.
    /// </summary>
    /// <remarks>
    ///     A computed column derived from the code table would satisfy every read in the test above
    ///     while reintroducing exactly the read-time coupling V-23 removes, and it would do so
    ///     invisibly — the property still reads as a plain string.
    /// </remarks>
    [Theory]
    [InlineData(nameof(StudentAttendance.AttendCode))]
    [InlineData(nameof(StudentAttendance.AttendCodeDescription))]
    [InlineData(nameof(StudentAttendance.IsAbsent))]
    [InlineData(nameof(StudentAttendance.IsExcused))]
    public void Model_SnapshotColumnsAreStoredNotComputed(string propertyName)
    {
        IProperty property = ModelFactory.Create()
            .FindEntityType(typeof(StudentAttendance))!
            .FindProperty(propertyName)!;

        Assert.Equal(ValueGenerated.Never, property.ValueGenerated);
        Assert.Null(property.GetComputedColumnSql());
        Assert.Null(property.GetDefaultValueSql());
    }

    /// <summary>
    ///     The snapshot's description column is at least as wide as the column it copies from.
    /// </summary>
    /// <remarks>
    ///     If F01c widens <c>AttendanceCode.Description</c> and F01d does not follow, every save of a
    ///     long description truncates the snapshot — and Postgres raises <c>22001</c> rather than
    ///     truncating silently, so the first failure lands on a user recording attendance rather than
    ///     on whoever widened the column.
    /// </remarks>
    [Fact]
    public void Model_AttendCodeDescriptionIsAtLeastAsWideAsItsSource()
    {
        IModel model = ModelFactory.Create();

        int? source = model.FindEntityType(typeof(AttendanceCode))!
            .FindProperty(nameof(AttendanceCode.Description))!.GetMaxLength();

        int? snapshot = model.FindEntityType(typeof(StudentAttendance))!
            .FindProperty(nameof(StudentAttendance.AttendCodeDescription))!.GetMaxLength();

        Assert.NotNull(source);
        Assert.NotNull(snapshot);
        Assert.True(
            snapshot >= source,
            $"AttendanceCode.Description holds {source} characters but the snapshot column "
            + $"StudentAttendance.AttendCodeDescription holds {snapshot}. Every save of a description "
            + "longer than the snapshot would fail at write time.");
    }

    /// <summary>
    ///     The snapshot's code column is at least as wide as <c>AttendanceCode.Value</c>, for the same
    ///     reason.
    /// </summary>
    [Fact]
    public void Model_AttendCodeIsAtLeastAsWideAsItsSource()
    {
        IModel model = ModelFactory.Create();

        int? source = model.FindEntityType(typeof(AttendanceCode))!
            .FindProperty(nameof(AttendanceCode.Value))!.GetMaxLength();

        int? snapshot = model.FindEntityType(typeof(StudentAttendance))!
            .FindProperty(nameof(StudentAttendance.AttendCode))!.GetMaxLength();

        Assert.NotNull(source);
        Assert.NotNull(snapshot);
        Assert.True(snapshot >= source, $"AttendanceCode.Value holds {source}, the snapshot holds {snapshot}.");
    }
}
