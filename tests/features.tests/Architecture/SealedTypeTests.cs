using System.Reflection;
using System.Runtime.CompilerServices;
using api;
using domain.Abstraction;
using infra.persistence.postgre;
using infra.persistence.sql;
using service.defaults;

namespace features.tests.Architecture;

/// <summary>
///     Asserts conventions §3's "all concrete types <c>sealed</c>" over every production assembly.
/// </summary>
/// <remarks>
///     <c>CA1852</c> is cited as the mechanism and does not implement the rule. It fires only when a
///     type "has no subtypes in its containing assembly <b>and is not externally visible</b>", so
///     every <c>public</c> class — entities, requests, responses, endpoints, exceptions, the entire
///     surface other assemblies bind to — is outside its scope by design. Verified by compiling an
///     unsealed <c>public</c> class and an unsealed <c>internal</c> class side by side with
///     <c>AnalysisLevel=latest-Recommended</c>: only the internal one produced CA1852.
///     <para>
///         No analyzer covers the public half. The rule is a project convention rather than a
///         framework one, and the only mechanism that reaches it is reflection over the built
///         assemblies — which is what this test is. It runs after compilation rather than during it,
///         so it reports one build later than an analyzer would; that is the honest cost, and it is
///         still a mechanism rather than a review habit.
///     </para>
///     <para>
///         Exemptions are narrow and each is justified below. They are enumerated in
///         <see cref="Exempt" /> so an exemption is a visible edit rather than an absence.
///     </para>
/// </remarks>
public sealed class SealedTypeTests
{
    public static TheoryData<string> ProductionAssemblies()
    {
        TheoryData<string> data = [];

        foreach (Assembly assembly in Assemblies())
            data.Add(assembly.GetName().Name!);

        return data;
    }

    [Theory]
    [MemberData(nameof(ProductionAssemblies))]
    public void ConcreteTypes_AreSealed(string assemblyName)
    {
        Assembly assembly = Assemblies().Single(candidate => candidate.GetName().Name == assemblyName);

        string[] unsealed = assembly.GetTypes()
            .Where(IsConcreteClass)
            .Where(type => !type.IsSealed)
            .Where(type => !Exempt(type))
            .Select(type => type.FullName ?? type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(unsealed.Length == 0,
            $"{assemblyName} declares unsealed concrete types: {string.Join(", ", unsealed)}. "
            + "Conventions §3 requires every concrete type to be sealed; CA1852 does not cover the "
            + "public ones (O-38).");
    }

    /// <summary>
    ///     A filter that excluded everything would make the assertion above vacuous, which is the
    ///     defect class this suite exists for. This pins that real types are reaching it.
    /// </summary>
    /// <remarks>
    ///     Asserted in aggregate rather than per assembly, because <c>infra.persistence.sql</c>
    ///     legitimately declares no concrete class at all — it is the <c>IDbContext</c> abstraction and
    ///     nothing else. A per-assembly threshold would fail on it and invite the threshold to be
    ///     dropped to zero, which is the assertion not being made.
    /// </remarks>
    [Fact]
    public void ConcreteTypes_AreActuallyBeingInspected()
    {
        int inspected = Assemblies()
            .SelectMany(assembly => assembly.GetTypes())
            .Count(type => IsConcreteClass(type) && !Exempt(type));

        Assert.True(inspected > 20, $"Only {inspected} concrete classes reached the seal check across "
                                    + "six assemblies; the filter is excluding almost everything.");
    }

    /// <summary>
    ///     Every assembly named must actually load and expose types — a renamed or dropped anchor
    ///     would otherwise remove a whole assembly from the check with nothing to show for it.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProductionAssemblies))]
    public void ProductionAssembly_LoadsAndExposesTypes(string assemblyName)
    {
        Assembly assembly = Assemblies().Single(candidate => candidate.GetName().Name == assemblyName);

        Assert.NotEmpty(assembly.GetTypes());
    }

    /// <summary>
    ///     The six assemblies the convention covers, anchored by a type in each so the runtime has
    ///     actually loaded them. Enumerating <c>AppDomain.CurrentDomain.GetAssemblies()</c> instead
    ///     would silently skip any assembly nothing had touched yet.
    /// </summary>
    private static Assembly[] Assemblies() =>
    [
        typeof(BaseEntity).Assembly,
        typeof(IDbContext).Assembly,
        typeof(ISparkrockRwcBuilder).Assembly,
        typeof(features.ServiceExtensions).Assembly,
        typeof(SparkrockRwcDbContext).Assembly,
        typeof(api.ServiceExtensions).Assembly
    ];

    private static bool IsConcreteClass(Type type) =>
        type.IsClass
        && !type.IsAbstract // also excludes static classes, which are abstract and sealed
        && !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
        && !type.Name.StartsWith('<');

    private static bool Exempt(Type type)
    {
        // Nested closures, iterator state machines and <PrivateImplementationDetails> are compiler
        // output; the attribute sits on the outermost generated type, not on every nested one.
        for (Type? declaring = type.DeclaringType; declaring is not null; declaring = declaring.DeclaringType)
        {
            if (declaring.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
                || declaring.Name.StartsWith('<'))
                return true;
        }

        // EF Core scaffolds migrations and the model snapshot as unsealed partial classes and will do
        // so again for every future migration. Hand-editing them risks diverging the snapshot from the
        // model, which is a worse failure than an unsealed generated type. Matched by base type name
        // rather than by directory, so a migration moved elsewhere is still covered.
        for (Type? baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (baseType.FullName is "Microsoft.EntityFrameworkCore.Migrations.Migration"
                or "Microsoft.EntityFrameworkCore.Infrastructure.ModelSnapshot")
                return true;
        }

        // Top-level statements synthesise `internal partial class Program`. Sealing it is not
        // expressible from Program.cs, and the type has no members to inherit.
        return type is { Name: "Program", Namespace: null };
    }
}
