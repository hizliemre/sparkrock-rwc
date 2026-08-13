using domain.Abstraction;
using domain.AttendanceCodes;
using domain.Schools;
using domain.SchoolTerms;
using domain.Students;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace features.tests.Model;

/// <summary>
///     Pins the reference schema before the migration is authored.
/// </summary>
/// <remarks>
///     Every name here is quoted somewhere else — index names by the constraint-to-error-code
///     mapping, table names by the migration. Asserting them at the model tier is what stops the two
///     drifting apart silently, since a renamed index produces a mapping that matches nothing rather
///     than an error.
/// </remarks>
public sealed class ReferenceModelTests
{
    private static readonly IModel Model = ModelFactory.Create();

    private static IEntityType Entity<T>() => Model.FindEntityType(typeof(T))!;

    [Theory]
    [InlineData(typeof(School), "schools")]
    [InlineData(typeof(Student), "students")]
    [InlineData(typeof(AttendanceCode), "attendance_codes")]
    [InlineData(typeof(SchoolTerm), "school_terms")]
    public void Model_MapsEntityToItsPinnedTableName(Type entity, string expected)
    {
        Assert.Equal(expected, Model.FindEntityType(entity)!.GetTableName());
    }

    /// <summary>
    ///     Reference entities carry no soft-delete state, so no query filter is generated and no
    ///     filtered subquery is joined into projections that reach them.
    /// </summary>
    [Theory]
    [InlineData(typeof(School))]
    [InlineData(typeof(Student))]
    [InlineData(typeof(AttendanceCode))]
    [InlineData(typeof(SchoolTerm))]
    public void Model_ReferenceEntitiesHaveNoSoftDeleteColumnsAndNoQueryFilter(Type entity)
    {
        IEntityType entityType = Model.FindEntityType(entity)!;

        Assert.False(typeof(SoftDeletableEntity).IsAssignableFrom(entity));
        Assert.Null(entityType.FindProperty("IsDeleted"));
        Assert.Null(entityType.FindProperty("DeletedAt"));
        Assert.Null(entityType.FindProperty("DeletedBy"));
        Assert.Null(entityType.GetQueryFilter());
    }

    /// <summary>
    ///     Unique, not merely indexed: the import matches on this column, and a plain index lets a
    ///     re-run duplicate every row and silently double every absence recount.
    /// </summary>
    [Theory]
    [InlineData(typeof(School), "ix_schools_legacy_id")]
    [InlineData(typeof(Student), "ix_students_legacy_id")]
    [InlineData(typeof(AttendanceCode), "ix_attendance_codes_legacy_id")]
    [InlineData(typeof(SchoolTerm), "ix_school_terms_legacy_id")]
    public void Model_LegacyIdIndexIsUniqueAndFilteredWithASnakeCasedPredicate(Type entity, string indexName)
    {
        IIndex index = Model.FindEntityType(entity)!.GetIndexes()
            .Single(i => i.GetDatabaseName() == indexName);

        Assert.True(index.IsUnique);

        // Written verbatim. The naming convention rewrites columns, indexes and keys but copies an
        // index filter through untouched, so a PascalCase predicate produces DDL naming a column
        // that does not exist.
        Assert.Equal("legacy_id IS NOT NULL", index.GetFilter());
    }

    [Fact]
    public void Model_EveryLegacyEntityDeclaresItsLegacyIndex()
    {
        IEntityType[] legacyEntities = Model.GetEntityTypes()
            .Where(e => typeof(ILegacyEntity).IsAssignableFrom(e.ClrType))
            .ToArray();

        Assert.NotEmpty(legacyEntities);

        foreach (IEntityType entityType in legacyEntities)
        {
            Assert.True(
                entityType.GetIndexes().Any(i => i.GetDatabaseName() == $"ix_{entityType.GetTableName()}_legacy_id"),
                $"{entityType.ClrType.Name} implements ILegacyEntity but declares no unique legacy index.");
        }
    }

    [Fact]
    public void Model_SchoolTimeZoneIdIsRequiredAndBounded()
    {
        IProperty property = Entity<School>().FindProperty(nameof(School.TimeZoneId))!;

        Assert.False(property.IsNullable);
        Assert.Equal(64, property.GetMaxLength());
    }

    /// <summary>
    ///     No database default. A <c>DEFAULT 10</c> column would be a second copy of a rule that
    ///     already lives in exactly one place.
    /// </summary>
    [Fact]
    public void Model_SchoolAbsenceAlertThresholdIsNullableWithNoDatabaseDefault()
    {
        IProperty property = Entity<School>().FindProperty(nameof(School.AbsenceAlertThreshold))!;

        Assert.True(property.IsNullable);
        Assert.Null(property.GetDefaultValue());
        Assert.Null(property.GetDefaultValueSql());
    }

    /// <summary>
    ///     Unfiltered, deliberately: deactivating a code never frees its value for reuse.
    /// </summary>
    [Fact]
    public void Model_AttendanceCodeValueIndexIsUniqueAndUnfiltered()
    {
        IIndex index = Entity<AttendanceCode>().GetIndexes()
            .Single(i => i.GetDatabaseName() == "ix_attendance_codes_value");

        Assert.True(index.IsUnique);
        Assert.Null(index.GetFilter());
    }

    [Fact]
    public void Model_AttendanceCodeValueIsBoundedToFiveCharacters()
    {
        Assert.Equal(5, Entity<AttendanceCode>().FindProperty(nameof(AttendanceCode.Value))!.GetMaxLength());
    }

    /// <summary>
    ///     EF defaults a required relationship to Cascade. Left at the default, removing one school
    ///     physically deletes every student in it.
    /// </summary>
    [Theory]
    [InlineData(typeof(Student), "fk_students_schools_school_id")]
    [InlineData(typeof(SchoolTerm), "fk_school_terms_schools_school_id")]
    public void Model_SchoolForeignKeyIsRestrictNotCascade(Type entity, string constraintName)
    {
        IForeignKey foreignKey = Model.FindEntityType(entity)!.GetForeignKeys()
            .Single(fk => fk.GetConstraintName() == constraintName);

        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
    }

    /// <summary>
    ///     No navigations anywhere in the reference model. A navigation is what makes the soft-delete
    ///     filter emit an <c>INNER JOIN</c> into an otherwise unrelated projection.
    /// </summary>
    [Theory]
    [InlineData(typeof(School))]
    [InlineData(typeof(Student))]
    [InlineData(typeof(AttendanceCode))]
    [InlineData(typeof(SchoolTerm))]
    public void Model_ReferenceEntitiesDeclareNoNavigationProperties(Type entity)
    {
        Assert.Empty(Model.FindEntityType(entity)!.GetNavigations());
    }

    [Fact]
    public void Model_StudentHasNoDateOfBirth()
    {
        Assert.Null(Entity<Student>().FindProperty("DateOfBirth"));
    }

    [Theory]
    [InlineData(typeof(Student))]
    [InlineData(typeof(SchoolTerm))]
    public void Model_SchoolScopedEntitiesImplementTheScopeInterface(Type entity)
    {
        Assert.True(typeof(ISchoolScoped).IsAssignableFrom(entity));
    }

    [Fact]
    public void Model_StudentGradeIsNullable()
    {
        Assert.True(Entity<Student>().FindProperty(nameof(Student.Grade))!.IsNullable);
    }

    [Theory]
    [InlineData(nameof(SchoolTerm.StartDate))]
    [InlineData(nameof(SchoolTerm.EndDate))]
    public void Model_SchoolTermDatesAreDateOnly(string propertyName)
    {
        Assert.Equal(typeof(DateOnly), Entity<SchoolTerm>().FindProperty(propertyName)!.ClrType);
    }

    [Fact]
    public void Model_SchoolTermHasTheDateRangeIndexSupportingOverlapChecks()
    {
        Assert.Contains(
            Entity<SchoolTerm>().GetIndexes(),
            i => i.GetDatabaseName() == "ix_school_terms_school_id_start_date_end_date");
    }
}
