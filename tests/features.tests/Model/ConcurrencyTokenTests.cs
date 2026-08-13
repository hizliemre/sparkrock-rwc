using domain.Attendance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace features.tests.Model;

/// <summary>
///     Pins the optimistic-concurrency token to the one shape that actually protects anything.
/// </summary>
/// <remarks>
///     <para>
///         On Npgsql a <see cref="uint" /> shadow property marked <c>IsRowVersion</c> maps to
///         <c>xmin</c>, the system column Postgres updates on every write. That is what makes the
///         <c>WHERE</c> clause of an update discriminate.
///     </para>
///     <para>
///         Declared as <c>byte[]</c> instead — the spelling carried over from SQL Server, and the one
///         an autocomplete or a half-remembered tutorial will suggest — it still compiles, still
///         reports <c>IsConcurrencyToken</c>, and still produces a migration. It creates a real
///         <c>bytea</c> column that <b>nothing ever writes</b>. Every token comparison then matches
///         the value it was given, every check passes, and a lost update goes through silently: no
///         exception, no retry, no failing test. VC-28 records the verification.
///     </para>
///     <para>
///         This file exists because that is not a hypothetical. Substituting <c>byte[]</c> in the
///         configuration left the entire model suite green — the migration DDL assertion reads the
///         already-generated migration file, so it does not see a model change until someone
///         regenerates it, and nothing else looked at the token at all.
///     </para>
///     <para>
///         The assertions run over every entity in the model rather than a named list, so a second
///         entity that acquires a token is covered without an edit — and one that acquires the wrong
///         kind fails immediately.
///     </para>
/// </remarks>
public sealed class ConcurrencyTokenTests
{
    private const string ExpectedSystemColumn = "xmin";

    private static readonly IModel Model = ModelFactory.Create();

    private static IEnumerable<(IEntityType Entity, IProperty Property)> ConcurrencyTokens() =>
        Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties().Select(p => (Entity: e, Property: p)))
            .Where(x => x.Property.IsConcurrencyToken);

    public static TheoryData<string> EntitiesWithAToken()
    {
        TheoryData<string> data = [];
        foreach ((IEntityType entity, IProperty _) in ConcurrencyTokens())
            data.Add(entity.ClrType.FullName!);

        return data;
    }

    /// <summary>
    ///     Guards every theory here against passing vacuously: with no token declared anywhere, a
    ///     member-data theory has no cases and reports success.
    /// </summary>
    [Fact]
    public void Model_DeclaresAtLeastOneConcurrencyToken()
    {
        Assert.NotEmpty(ConcurrencyTokens());
    }

    /// <summary>
    ///     The summary is the row DEC-14's whole retry design protects, so its token is named
    ///     explicitly rather than left to the sweep above — deleting the property would otherwise
    ///     just remove a test case.
    /// </summary>
    [Fact]
    public void Model_SummaryDeclaresAConcurrencyToken()
    {
        IProperty? version = Model.FindEntityType(typeof(StudentAttendanceSummary))!.FindProperty("Version");

        Assert.NotNull(version);
        Assert.True(
            version.IsConcurrencyToken,
            "StudentAttendanceSummary has no concurrency token. DEC-14 resolves the lost update that "
            + "L-12 left in the legacy recount entirely through this token; without it the retry loop "
            + "has nothing to detect and two concurrent submissions silently overwrite each other.");
    }

    [Theory]
    [MemberData(nameof(EntitiesWithAToken))]
    public void Model_ConcurrencyTokenIsUIntMappedToTheSystemColumn(string entityTypeName)
    {
        (IEntityType entity, IProperty property) = ConcurrencyTokens()
            .Single(x => x.Entity.ClrType.FullName == entityTypeName);

        Assert.True(
            property.ClrType == typeof(uint),
            $"The concurrency token on {entity.ClrType.Name} is {property.ClrType.Name}, not uint. "
            + "Only uint maps to Postgres's xmin system column. byte[] compiles and produces a bytea "
            + "column that nothing ever writes, so every concurrency check passes trivially and a "
            + "lost update goes through with no error (VC-28).");

        Assert.Equal(ExpectedSystemColumn, property.GetColumnName());

        // xmin is maintained by the server, never by EF. Anything else here means the token is a
        // column the application is expected to bump, which nothing does.
        Assert.Equal(ValueGenerated.OnAddOrUpdate, property.ValueGenerated);
    }

    /// <summary>
    ///     No table gains a real column for the token.
    /// </summary>
    /// <remarks>
    ///     This is the assertion that fails on the <c>byte[]</c> spelling even if someone also
    ///     renames the property, because the failure it catches is a *column being created* rather
    ///     than a name being wrong. <c>xmin</c> is a system column and must never appear in a
    ///     <c>CREATE TABLE</c>.
    /// </remarks>
    [Fact]
    public void Model_NoTableDeclaresARealColumnForItsToken()
    {
        foreach ((IEntityType entity, IProperty property) in ConcurrencyTokens())
        {
            string? columnType = property.GetColumnType();

            Assert.True(
                columnType is null or "xid",
                $"The concurrency token on {entity.ClrType.Name} maps to column type '{columnType}'. "
                + "A token with a storage type of its own is a real column, which means it is not "
                + "xmin — and a real column nothing writes protects nothing.");
        }
    }
}
