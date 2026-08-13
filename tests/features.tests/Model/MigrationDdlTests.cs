namespace features.tests.Model;

/// <summary>
///     Asserts the generated migration declares the DDL the model promises.
/// </summary>
/// <remarks>
///     Read from the migration file rather than from model metadata deliberately. Check constraints
///     declared through the table builder do not surface through the entity-type accessor, so a
///     metadata assertion silently passes on an empty collection — the migration is the artifact that
///     actually reaches the database, and it is what the constraint-to-error-code mapping quotes.
/// </remarks>
public sealed class MigrationDdlTests
{
    private static readonly string Migration = ReadReferenceModelMigration();

    [Theory]
    [InlineData("ck_schools_absence_alert_threshold_positive", "absence_alert_threshold IS NULL OR absence_alert_threshold >= 1")]
    [InlineData("ck_attendance_codes_value_upper", "value = upper(value)")]
    [InlineData("ck_school_terms_end_date_not_before_start_date", "end_date >= start_date")]
    public void Migration_DeclaresCheckConstraintWithItsPinnedNameAndPredicate(string name, string predicate)
    {
        Assert.Contains($"table.CheckConstraint(\"{name}\", \"{predicate}\")", Migration, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The naming convention rewrites columns, indexes and keys but copies an index filter through
    ///     untouched, so a PascalCase predicate produces DDL naming a column that does not exist.
    /// </summary>
    [Fact]
    public void Migration_WritesLegacyIndexFiltersInSnakeCase()
    {
        Assert.Equal(4, Occurrences(Migration, "filter: \"legacy_id IS NOT NULL\""));
        Assert.DoesNotContain("LegacyId IS NOT NULL", Migration, StringComparison.Ordinal);
    }

    /// <summary>
    ///     EF defaults a required relationship to cascade, under which removing one school physically
    ///     deletes every student in it.
    /// </summary>
    [Fact]
    public void Migration_MakesEverySchoolForeignKeyRestrict()
    {
        Assert.Equal(2, Occurrences(Migration, "onDelete: ReferentialAction.Restrict"));
        Assert.DoesNotContain("ReferentialAction.Cascade", Migration, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("schools")]
    [InlineData("students")]
    [InlineData("attendance_codes")]
    [InlineData("school_terms")]
    public void Migration_CreatesTheSnakeCasedPluralTable(string tableName)
    {
        Assert.Contains($"name: \"{tableName}\"", Migration, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Reference entities carry no soft-delete state, so the migration must not create the columns.
    /// </summary>
    [Theory]
    [InlineData("is_deleted")]
    [InlineData("deleted_at")]
    [InlineData("deleted_by")]
    public void Migration_CreatesNoSoftDeleteColumnOnReferenceTables(string columnName)
    {
        Assert.DoesNotContain($"{columnName} = table.Column", Migration, StringComparison.Ordinal);
    }

    private static int Occurrences(string haystack, string needle)
    {
        int count = 0;
        int index = haystack.IndexOf(needle, StringComparison.Ordinal);

        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static string ReadReferenceModelMigration()
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);

        while (directory.Parent is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;

        string migrations = Path.Combine(
            directory.FullName, "src", "infra.persistence.postgre", "Migrations");

        string file = Directory.GetFiles(migrations, "*_ReferenceModel.cs").Single();

        return File.ReadAllText(file);
    }
}
