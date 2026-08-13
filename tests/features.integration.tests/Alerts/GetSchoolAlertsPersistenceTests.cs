using domain.Attendance;
using domain.Schools;
using domain.Students;
using features.Alerts;
using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;

namespace features.integration.tests.Alerts;

/// <summary>
///     Whether the worklist query <b>translates</b>. Nothing else here is behavioural — every
///     behavioural assertion is at the handler tier and conventions §6 forbids writing it twice.
/// </summary>
/// <remarks>
///     The failure mode this tier exists for is VC-31's: an expression that compiles, returns the
///     right rows on EF InMemory because InMemory evaluates in LINQ-to-Objects, and throws
///     <see cref="InvalidOperationException" /> on Npgsql — surfacing as a 500. Three things in this
///     query are candidates: the whole-value <c>SchoolYear</c> comparison, the
///     <c>?? AbsenceRules.DefaultThreshold</c> coalesce, and a <c>Where</c> composed <em>over</em> a
///     three-table projection, which is how the drift filter and the drift flag are kept as one
///     expression.
///     <para>
///         The database is shared across the collection: this class allocates fresh
///         <see cref="Guid" />s and school years and asserts only about its own rows.
///     </para>
/// </remarks>
[Collection(IntegrationTestCollectionDefinition.Name)]
public sealed class GetSchoolAlertsPersistenceTests(PostgresContainerFixture fixture)
{
    [Fact]
    public async Task ThresholdDrift_TranslatesToSql()
    {
        const int schoolYearStart = 2091;

        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();
        School school = await AlertFixture.SchoolAsync(dbContext, threshold: 12);
        Student stranded = await AlertFixture.StudentAsync(dbContext, school.Id);
        Student aligned = await AlertFixture.StudentAsync(dbContext, school.Id);

        Guid strandedAlertId = (await AlertFixture.OpenAlertAsync(
            dbContext, stranded.Id, school.Id, schoolYearStart, thresholdAtRaise: 10)).Id;
        await AlertFixture.OpenAlertAsync(
            dbContext, aligned.Id, school.Id, schoolYearStart, thresholdAtRaise: 12);

        List<GetSchoolAlerts.Row> rows = await GetSchoolAlerts
            .AlertRows(dbContext, school.Id, GetSchoolAlerts.StatusFilter.Open, schoolYearStart, thresholdDrift: true)
            .ToListAsync(CancellationToken.None);

        Assert.Equal(strandedAlertId, Assert.Single(rows).Id);
    }

    /// <summary>
    ///     <b>The null-threshold case, against real SQL.</b> The predicate is
    ///     <c>threshold_at_raise &lt;&gt; COALESCE(absence_alert_threshold, 10)</c>; drop the coalesce
    ///     and the comparison is against <c>NULL</c>, which in Postgres is neither true nor false, so
    ///     <em>no</em> row comes back — a stranded-alert query that silently reports nothing stranded
    ///     is exactly DEC-18's "the rule silently strands alerts".
    /// </summary>
    [Fact]
    public async Task ThresholdDrift_WhenSchoolThresholdIsNull_CoalescesToTheDomainDefaultInSql()
    {
        const int schoolYearStart = 2092;

        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();
        School school = await AlertFixture.SchoolAsync(dbContext, threshold: null);
        Student atDefault = await AlertFixture.StudentAsync(dbContext, school.Id);
        Student drifted = await AlertFixture.StudentAsync(dbContext, school.Id);

        await AlertFixture.OpenAlertAsync(
            dbContext, atDefault.Id, school.Id, schoolYearStart,
            thresholdAtRaise: AbsenceRules.DefaultThreshold);
        Guid driftedAlertId = (await AlertFixture.OpenAlertAsync(
            dbContext, drifted.Id, school.Id, schoolYearStart,
            thresholdAtRaise: AbsenceRules.DefaultThreshold + 5)).Id;

        IQueryable<GetSchoolAlerts.Row> query = GetSchoolAlerts.AlertRows(
            dbContext, school.Id, GetSchoolAlerts.StatusFilter.Open, schoolYearStart, thresholdDrift: true);

        Assert.Equal(driftedAlertId, Assert.Single(await query.ToListAsync(CancellationToken.None)).Id);

        // The coalesce is in the SQL rather than applied after materialisation — otherwise the
        // filter would run in memory over an unbounded read and page.totalItems would count the
        // unfiltered set.
        Assert.Contains("COALESCE", query.ToQueryString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     DEC-16's access rule against real joins. Three joins and a <c>where</c> on the
    ///     <em>middle</em> table is where a join-direction error surfaces, and it is invisible on any
    ///     data set without a transfer.
    /// </summary>
    [Fact]
    public async Task List_ScopesByStudentCurrentSchool_AgainstRealJoins()
    {
        const int schoolYearStart = 2093;

        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();
        School former = await AlertFixture.SchoolAsync(dbContext, threshold: 10);
        School current = await AlertFixture.SchoolAsync(dbContext, threshold: 15);

        // Transferred: the student is at `current`, the alert still names `former`.
        Student student = await AlertFixture.StudentAsync(dbContext, current.Id);
        Guid alertId = (await AlertFixture.OpenAlertAsync(
            dbContext, student.Id, former.Id, schoolYearStart, thresholdAtRaise: 10)).Id;

        List<GetSchoolAlerts.Row> receiving = await GetSchoolAlerts
            .AlertRows(dbContext, current.Id, GetSchoolAlerts.StatusFilter.Open, schoolYearStart, thresholdDrift: false)
            .ToListAsync(CancellationToken.None);

        List<GetSchoolAlerts.Row> formerSchool = await GetSchoolAlerts
            .AlertRows(dbContext, former.Id, GetSchoolAlerts.StatusFilter.Open, schoolYearStart, thresholdDrift: false)
            .ToListAsync(CancellationToken.None);

        GetSchoolAlerts.Row row = Assert.Single(receiving);

        Assert.Equal(alertId, row.Id);
        Assert.False(row.IsCurrentSchoolOfRecord);

        // The receiving school's threshold governs (DEC-16), not the one the alert was raised under.
        Assert.Equal(15, row.CurrentThreshold);

        Assert.Empty(formerSchool);
    }

    /// <summary>
    ///     VC-31, in the position where it is a 500 rather than a build error: the <c>?schoolYear=</c>
    ///     predicate compares the <b>whole converted value</b>. The illegal form
    ///     (<c>alert.SchoolYearStart.StartYear == year</c> in a <c>Where</c>) compiles and passes every
    ///     handler-tier test.
    /// </summary>
    [Fact]
    public async Task SchoolYearFilter_ComparesTheWholeValueInSql()
    {
        const int schoolYearStart = 2094;

        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();
        School school = await AlertFixture.SchoolAsync(dbContext, threshold: 10);
        Student student = await AlertFixture.StudentAsync(dbContext, school.Id);

        Guid wanted = (await AlertFixture.OpenAlertAsync(
            dbContext, student.Id, school.Id, schoolYearStart)).Id;
        await AlertFixture.OpenAlertAsync(dbContext, student.Id, school.Id, schoolYearStart + 1);

        IQueryable<GetSchoolAlerts.Row> query = GetSchoolAlerts.AlertRows(
            dbContext, school.Id, GetSchoolAlerts.StatusFilter.Open, schoolYearStart, thresholdDrift: false);

        Assert.Equal(wanted, Assert.Single(await query.ToListAsync(CancellationToken.None)).Id);

        // Compared against a parameter with nothing wrapped around the column, so the predicate stays
        // sargable against ix_student_alerts_student_id_school_year_start.
        Assert.Contains("school_year_start = @", query.ToQueryString(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     The default sort, executed by the database rather than by LINQ-to-Objects. Newest first,
    ///     ending in <c>Id</c> so the order is total (VC-27).
    /// </summary>
    [Fact]
    public async Task List_OrdersByRaisedAtDescendingThenIdInSql()
    {
        const int schoolYearStart = 2095;

        await using SparkrockRwcDbContext dbContext = fixture.CreateDbContext();
        School school = await AlertFixture.SchoolAsync(dbContext, threshold: 10);
        Student student = await AlertFixture.StudentAsync(dbContext, school.Id);
        await AlertFixture.OpenAlertAsync(dbContext, student.Id, school.Id, schoolYearStart);

        string sql = GetSchoolAlerts
            .AlertRows(dbContext, school.Id, GetSchoolAlerts.StatusFilter.Open, schoolYear: null, thresholdDrift: false)
            .ToQueryString();

        int orderBy = sql.IndexOf("ORDER BY", StringComparison.Ordinal);

        Assert.True(orderBy >= 0, $"The generated SQL has no ORDER BY clause:{Environment.NewLine}{sql}");
        Assert.Contains("created_at DESC", sql[orderBy..], StringComparison.Ordinal);
    }
}
