using infra.persistence.postgre.ErrorTranslation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace features.tests.Model;

/// <summary>
///     Asserts every registry key names a constraint the schema actually creates.
/// </summary>
/// <remarks>
///     <para>
///         The registry is a dictionary of exact strings and <c>TryResolve</c> is an ordinal lookup,
///         so a key that names nothing is not an error — it is a miss. The translator then returns
///         <see langword="null" />, the provider exception is rethrown unchanged, and the caller gets
///         a 500 carrying a Postgres message instead of the mapped 409. Nothing fails until the
///         constraint is violated in production, and at that point the failure looks like a bug in
///         the handler rather than a typo in a table.
///     </para>
///     <para>
///         This is not hypothetical. Two of these names were renamed while fixing VC-36 — the
///         alert-episode index exceeded Postgres's 63-character identifier limit — and F01d's
///         specification table still carried the pre-rename spellings
///         (<c>ix_student_attendance_summaries_…</c>, <c>ix_attendance_submission_logs_…</c>). Copied
///         into the registry, both would have resolved nothing.
///     </para>
///     <para>
///         The reverse direction is deliberately not asserted. Plenty of indexes are unmapped on
///         purpose: non-unique ones cannot be violated, and the foreign keys are all
///         <c>Restrict</c>, where a violation is a bug rather than a race and a mapped 409 would
///         present it as the caller's fault.
///     </para>
/// </remarks>
public sealed class ConstraintRegistryTests
{
    private static readonly HashSet<string> SchemaIndexNames = ModelFactory.Create()
        .GetEntityTypes()
        .SelectMany(e => e.GetIndexes())
        .Select(i => i.GetDatabaseName())
        .Where(name => !string.IsNullOrEmpty(name))
        .ToHashSet(StringComparer.Ordinal)!;

    public static TheoryData<string> RegisteredConstraintNames()
    {
        TheoryData<string> data = [];
        foreach (string name in SchemaConstraintErrors.Mappings.Keys)
            data.Add(name);

        return data;
    }

    [Theory]
    [MemberData(nameof(RegisteredConstraintNames))]
    public void Registry_KeyNamesAnIndexTheSchemaCreates(string constraintName)
    {
        Assert.True(
            SchemaIndexNames.Contains(constraintName),
            $"The constraint registry maps '{constraintName}', but no index in the model declares that "
            + "name. The lookup is ordinal, so this key would never match: the violation would be "
            + "rethrown raw as a 500 rather than translated. Closest names in the model: "
            + string.Join(", ", SchemaIndexNames.Where(n => n.Contains("student") || n.Contains("submission"))
                .OrderBy(n => n, StringComparer.Ordinal)));
    }

    /// <summary>
    ///     A mapped constraint that is not unique can never be violated, so its row is dead weight
    ///     that reads as coverage.
    /// </summary>
    [Theory]
    [MemberData(nameof(RegisteredConstraintNames))]
    public void Registry_MappedIndexIsUnique(string constraintName)
    {
        IIndex index = ModelFactory.Create()
            .GetEntityTypes()
            .SelectMany(e => e.GetIndexes())
            .Single(i => i.GetDatabaseName() == constraintName);

        Assert.True(
            index.IsUnique,
            $"'{constraintName}' is mapped to an error code but is not a unique index, so it cannot "
            + "raise 23505 and the mapping can never fire.");
    }

    /// <summary>
    ///     Guards the test above against passing vacuously. An empty registry would satisfy every
    ///     theory in this class by having no cases at all.
    /// </summary>
    [Fact]
    public void Registry_IsNotEmpty()
    {
        Assert.NotEmpty(SchemaConstraintErrors.Mappings);
    }

    /// <summary>
    ///     The registry is what the composition root actually installs. Building it from the same
    ///     table the tests above assert over is what stops this passing while the application still
    ///     runs on <c>ConstraintErrorRegistry.Empty</c>.
    /// </summary>
    [Fact]
    public void Registry_ResolvesEveryMappingItDeclares()
    {
        ConstraintErrorRegistry registry = new(SchemaConstraintErrors.Mappings);

        foreach ((string name, ConstraintErrorMapping expected) in SchemaConstraintErrors.Mappings)
        {
            Assert.True(registry.TryResolve(name, out ConstraintErrorMapping? actual));
            Assert.Equal(expected, actual);
        }
    }

    /// <summary>
    ///     Every mapped code must be a declared constant, not a literal typed at the mapping site.
    /// </summary>
    [Fact]
    public void Registry_UsesDeclaredErrorCodeConstants()
    {
        string[] declared =
        [
            domain.Exceptions.ErrorCodes.Attendance.ConcurrentSubmission,
            domain.Exceptions.ErrorCodes.Attendance.DuplicateSubmission,
            domain.Exceptions.ErrorCodes.Alert.DuplicateOpenEpisode,
            domain.Exceptions.ErrorCodes.Import.DuplicateLegacyId,
        ];

        foreach (ConstraintErrorMapping mapping in SchemaConstraintErrors.Mappings.Values)
            Assert.Contains(mapping.ErrorCode, declared);
    }

    /// <summary>
    ///     Retryability is a behavioural claim, not a flag: DEC-14 spends its whole attempt bound on
    ///     anything marked retryable. A client-supplied key collides identically on every attempt, so
    ///     marking one retryable would burn the bound to return the same error.
    /// </summary>
    [Theory]
    [InlineData(SchemaConstraintErrors.Names.AttendanceStudentDate, true)]
    [InlineData(SchemaConstraintErrors.Names.SummaryStudentYear, true)]
    [InlineData(SchemaConstraintErrors.Names.AlertOpenEpisode, true)]
    [InlineData(SchemaConstraintErrors.Names.SubmissionIdempotencyKey, false)]
    [InlineData(SchemaConstraintErrors.Names.AttendanceLegacyId, false)]
    public void Registry_RetryabilityMatchesWhetherRetryingCanSucceed(string constraintName, bool retryable)
    {
        Assert.Equal(retryable, SchemaConstraintErrors.Mappings[constraintName].Retryable);
    }
}
