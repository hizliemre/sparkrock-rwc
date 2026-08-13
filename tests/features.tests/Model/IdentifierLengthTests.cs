using infra.persistence.postgre;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace features.tests.Model;

/// <summary>
///     Asserts no database identifier exceeds Postgres's 63-character limit.
/// </summary>
/// <remarks>
///     Postgres truncates a longer identifier and reports only a <c>NOTICE</c>, which
///     <c>dotnet ef database update</c> never surfaces. `HasDatabaseName` is not length-checked by
///     EF, so the name in the model and the name in the database silently diverge.
///     <para>
///         That divergence is not cosmetic. <c>PostgresException.ConstraintName</c> carries the
///         <em>truncated</em> form, and the constraint-to-error-code registry matches ordinally — so
///         a registry keyed on the declared name resolves nothing, the translator declines, and a raw
///         provider exception escapes to the client instead of the mapped conflict response. The
///         failure is invisible until the constraint is actually violated in production.
///     </para>
///     <para>
///         Read from the design-time model rather than <c>DbContext.Model</c>: check-constraint names
///         are absent from the read-optimised model, which throws when asked for them.
///     </para>
/// </remarks>
public sealed class IdentifierLengthTests
{
    private const int PostgresMaxIdentifierLength = 63;

    public static TheoryData<string, string> AllIdentifiers()
    {
        TheoryData<string, string> data = [];

        DbContextOptions<SparkrockRwcDbContext> options = new DbContextOptionsBuilder<SparkrockRwcDbContext>()
            .UseNpgsql("Host=model-only;Database=model-only")
            .UseSnakeCaseNamingConvention()
            .Options;

        using SparkrockRwcDbContext context = new(options);
        IModel model = context.GetService<IDesignTimeModel>().Model;

        foreach (IEntityType entityType in model.GetEntityTypes())
        {
            Add(data, "table", entityType.GetTableName());

            foreach (IKey key in entityType.GetKeys())
                Add(data, "key", key.GetName());

            foreach (IForeignKey foreignKey in entityType.GetForeignKeys())
                Add(data, "foreign key", foreignKey.GetConstraintName());

            foreach (IIndex index in entityType.GetIndexes())
                Add(data, "index", index.GetDatabaseName());

            foreach (ICheckConstraint constraint in entityType.GetCheckConstraints())
                Add(data, "check constraint", constraint.Name);

            foreach (IProperty property in entityType.GetProperties())
                Add(data, "column", property.GetColumnName());
        }

        return data;
    }

    private static void Add(TheoryData<string, string> data, string kind, string? name)
    {
        if (!string.IsNullOrEmpty(name))
            data.Add(kind, name);
    }

    [Theory]
    [MemberData(nameof(AllIdentifiers))]
    public void Identifier_FitsWithinThePostgresLimit(string kind, string name)
    {
        Assert.True(
            name.Length <= PostgresMaxIdentifierLength,
            $"The {kind} '{name}' is {name.Length} characters. Postgres truncates at "
            + $"{PostgresMaxIdentifierLength} and reports only a NOTICE, so the model and the database "
            + "would disagree about its name — and a constraint lookup keyed on the declared name "
            + "would silently never match.");
    }
}
