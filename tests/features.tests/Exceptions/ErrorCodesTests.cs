using System.Reflection;
using System.Text.RegularExpressions;
using domain.Exceptions;

namespace features.tests.Exceptions;

/// <summary>
///     Mechanises cross-reference check #4: every error code traces to a declared area.
/// </summary>
/// <remarks>
///     Written once here so every future <c>ErrorCodes.&lt;Area&gt;.cs</c> file inherits it. Twelve
///     workstreams adding codes to a shared vocabulary is exactly where a format drifts.
/// </remarks>
public sealed class ErrorCodesTests
{
    private static readonly HashSet<string> ClosedAreaSet =
    [
        "VALIDATION", "SCHOOL", "STUDENT", "ATTENDANCE", "ATTENDANCE_CODE",
        "TERM", "ALERT", "IMPORT", "SYSTEM"
    ];

    public static TheoryData<string, string, string> AllCodes()
    {
        TheoryData<string, string, string> data = [];

        foreach (Type area in typeof(ErrorCodes).GetNestedTypes(BindingFlags.Public | BindingFlags.Static))
        {
            foreach (FieldInfo field in area.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            {
                if (field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
                    data.Add(area.Name, field.Name, (string)field.GetRawConstantValue()!);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllCodes))]
    public void ErrorCodes_EveryValueIsAreaDotCondition(string areaName, string fieldName, string value)
    {
        Assert.True(
            Regex.IsMatch(value, "^[A-Z][A-Z_]*\\.[A-Z][A-Z_]*$"),
            $"{areaName}.{fieldName} is '{value}'; expected AREA.CONDITION in upper snake case.");
    }

    [Theory]
    [MemberData(nameof(AllCodes))]
    public void ErrorCodes_EveryAreaIsInTheClosedSet(string areaName, string fieldName, string value)
    {
        string area = value.Split('.')[0];

        Assert.True(
            ClosedAreaSet.Contains(area),
            $"{areaName}.{fieldName} declares area '{area}', which is not in conventions §5's closed set.");
    }

    [Theory]
    [MemberData(nameof(AllCodes))]
    public void ErrorCodes_NestedClassNameMatchesTheAreaSegment(string areaName, string fieldName, string value)
    {
        string expectedArea = ToUpperSnakeCase(areaName);
        string actualArea = value.Split('.')[0];

        Assert.True(
            expectedArea == actualArea,
            $"{areaName}.{fieldName} is '{value}', but the nested class name implies area '{expectedArea}'.");
    }

    /// <summary>
    ///     Every area in the closed set is declared by an area class.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the direction the rest of this file does not check, and its absence cost two
    ///         features a broken precondition. Every other assertion here iterates the codes that
    ///         <em>exist</em>, so an area named in the closed set with no class behind it satisfies all
    ///         of them vacuously — there are simply no cases for it.
    ///     </para>
    ///     <para>
    ///         That is exactly what happened to <c>ATTENDANCE_CODE</c>. It was listed in conventions
    ///         §5's closed set and in <see cref="ClosedAreaSet" />, two specifications recorded
    ///         <c>ErrorCodes.AttendanceCode.cs</c> as already shipped, and the file did not exist. The
    ///         suite was green throughout. `STUDENT` was the same story one feature later.
    ///     </para>
    ///     <para>
    ///         The consequence of this test is that an area cannot be added to the closed set ahead of
    ///         its class: the two land in the same commit, or the build is red. That is the intended
    ///         cost — a name reserved in a shared vocabulary with nothing behind it reads as shipped.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ErrorCodes_EveryAreaInTheClosedSetIsDeclaredByAnAreaClass()
    {
        HashSet<string> declared = AllCodes()
            .Select(row => ((string)row[2]!).Split('.')[0])
            .ToHashSet(StringComparer.Ordinal);

        string[] undeclared = ClosedAreaSet.Except(declared, StringComparer.Ordinal)
            .OrderBy(area => area, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            undeclared.Length == 0,
            $"Declared in the closed area set but by no area class: {string.Join(", ", undeclared)}. "
            + "Every other assertion in this file iterates codes that exist, so an area with no class "
            + "passes all of them by having no cases at all — which is how two features came to record "
            + "a missing ErrorCodes file as already shipped.");
    }

    [Fact]
    public void ErrorCodes_DeclaresNoConstantsOutsideAnAreaClass()
    {
        FieldInfo[] loose = typeof(ErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .ToArray();

        Assert.True(loose.Length == 0, $"Move to an area class: {string.Join(", ", loose.Select(f => f.Name))}");
    }

    private static string ToUpperSnakeCase(string pascalCase)
    {
        return Regex.Replace(pascalCase, "(?<!^)([A-Z])", "_$1").ToUpperInvariant();
    }
}
