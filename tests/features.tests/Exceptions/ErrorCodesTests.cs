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
