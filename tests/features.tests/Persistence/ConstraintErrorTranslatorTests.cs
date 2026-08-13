using domain;
using domain.Exceptions;
using infra.persistence.postgre;
using infra.persistence.postgre.ErrorTranslation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Npgsql;

namespace features.tests.Persistence;

/// <summary>
///     The handler tier for DEC-14's constraint translation. <c>Translate</c> is a pure function over
///     <c>(registry, DbUpdateException)</c> precisely so the mapping decisions are testable without a
///     server; the <c>catch</c> wiring firing against a real <c>23505</c> is integration-tier work
///     (conventions §6).
/// </summary>
public sealed class ConstraintErrorTranslatorTests
{
    private const string RetryableConstraint = "ix_student_attendance_summaries_student_id_school_year_start";
    private const string RetryableErrorCode = "ATTENDANCE.CONCURRENT_SUBMISSION";
    private const string RetryableMessage = "Another submission updated this student. Retry.";

    private const string PermanentConstraint = "ix_attendance_codes_value";
    private const string PermanentErrorCode = "ATTENDANCE_CODE.DUPLICATE_VALUE";
    private const string PermanentMessage = "An attendance code with that value already exists.";

    private static readonly IConstraintErrorRegistry Registry = new ConstraintErrorRegistry(
        new Dictionary<string, ConstraintErrorMapping>
        {
            [RetryableConstraint] = new(RetryableErrorCode, RetryableMessage, true),
            [PermanentConstraint] = new(PermanentErrorCode, PermanentMessage, false)
        });

    /// <summary>
    ///     A save can fail for reasons the provider never saw — the translation must not claim those.
    /// </summary>
    [Fact]
    public void Translate_WhenInnerIsNotPostgresException_ReturnsNull()
    {
        DbUpdateException source = new("An error occurred while saving.", new InvalidOperationException("not a provider error"));

        Assert.Null(ConstraintErrorTranslator.Translate(Registry, source));
    }

    /// <summary>
    ///     DEC-14: matching on <see cref="DbUpdateException" /> alone would retry a permanent FK or
    ///     check violation until the bound is exhausted. An unnamed or unknown constraint rethrows.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("ix_a_constraint_no_feature_has_registered")]
    public void Translate_WhenConstraintUnmapped_ReturnsNull(string? constraintName)
    {
        DbUpdateException source = new("An error occurred while saving.", PostgresError(constraintName));

        Assert.Null(ConstraintErrorTranslator.Translate(Registry, source));
    }

    /// <summary>
    ///     VC-29: the entries are the only route from <c>features</c> to the detach-or-reload recovery
    ///     a retry needs. Translating them away leaves every subsequent attempt failing identically.
    /// </summary>
    [Fact]
    public void Translate_WhenConstraintMappedAndRetryable_ReturnsConcurrencyConflictWithEntries()
    {
        using SparkrockRwcDbContext dbContext = InMemoryDbContextFactory.Create();
        TestEntity entity = new() { TestProperty = "value" };
        EntityEntry entry = dbContext.TestEntities.Add(entity);

        DbUpdateException source = new(
            "An error occurred while saving.",
            PostgresError(RetryableConstraint),
            new[] { entry });

        Exception? translated = ConstraintErrorTranslator.Translate(Registry, source);

        ConcurrencyConflictException conflict = Assert.IsType<ConcurrencyConflictException>(translated);
        Assert.Equal(RetryableConstraint, conflict.ConstraintName);
        Assert.Equal(RetryableErrorCode, conflict.ErrorCode);
        Assert.Equal(RetryableMessage, conflict.Message);
        Assert.Same(source, conflict.InnerException);
        Assert.Same(entity, Assert.Single(conflict.Entries).Entity);
    }

    /// <summary>
    ///     A permanent duplicate is a plain 409. It must not be a
    ///     <see cref="ConcurrencyConflictException" />, or the retry predicate replays it.
    /// </summary>
    [Fact]
    public void Translate_WhenConstraintMappedAndNotRetryable_ReturnsConflictException()
    {
        DbUpdateException source = new("An error occurred while saving.", PostgresError(PermanentConstraint));

        Exception? translated = ConstraintErrorTranslator.Translate(Registry, source);

        ConflictException conflict = Assert.IsType<ConflictException>(translated);
        Assert.Equal(PermanentErrorCode, conflict.ErrorCode);
        Assert.Equal(PermanentMessage, conflict.Message);
    }

    /// <summary>
    ///     It derives from <see cref="DbUpdateException" />, so a careless filter swallows it. F07's
    ///     <c>ex.Entries</c>/<c>ReloadAsync</c> recovery needs the original type to survive (VC-29).
    /// </summary>
    [Fact]
    public void Translate_WhenDbUpdateConcurrencyException_ReturnsNull()
    {
        DbUpdateConcurrencyException source = new("The database operation was expected to affect 1 row(s).");

        Assert.Null(ConstraintErrorTranslator.Translate(Registry, source));
    }

    /// <summary>
    ///     F01a ships the registry empty: every constraint in conventions §5's table belongs to an
    ///     entity that does not exist, and a speculative row would pin a name no migration created.
    /// </summary>
    [Fact]
    public void Registry_Empty_ResolvesNothing()
    {
        Assert.False(ConstraintErrorRegistry.Empty.TryResolve(RetryableConstraint, out ConstraintErrorMapping? mapping));
        Assert.Null(mapping);

        DbUpdateException source = new("An error occurred while saving.", PostgresError(RetryableConstraint));

        Assert.Null(ConstraintErrorTranslator.Translate(ConstraintErrorRegistry.Empty, source));
    }

    private static PostgresException PostgresError(string? constraintName) =>
        new(
            "duplicate key value violates unique constraint",
            "ERROR",
            "ERROR",
            PostgresErrorCodes.UniqueViolation,
            constraintName: constraintName);
}
