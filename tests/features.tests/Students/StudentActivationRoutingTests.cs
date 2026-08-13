using System.Text.RegularExpressions;
using features.tests.Security;

namespace features.tests.Students;

/// <summary>
///     Every activation change in F05 goes through <c>ActivationPolicy</c>, and no slice assigns
///     <c>IsActive</c> itself.
/// </summary>
/// <remarks>
///     <c>ActivationPolicy</c>'s own documentation states that the convention is enforced by being
///     <em>greppable</em>: "a slice changing <c>IsActive</c> without stating its privilege class is a
///     slice with no call to <c>ActivationPolicy.Apply</c>". Nothing greps. This does.
///     <para>
///         A source scan rather than reflection, because the thing being prohibited is a statement,
///         not a shape — <c>student.IsActive = false</c> and
///         <c>ActivationPolicy.Apply(student, false, …)</c> produce an identical entity. F05's
///         privilege is <c>SchoolScope</c> and performs no check today, so no behavioural test can
///         tell the two apart on the happy path; the difference only appears the day the rule gains a
///         privilege, which is exactly when nobody is looking.
///     </para>
///     <para>
///         Scoped to F05's own directory. F02, F03 and F04 own their equivalents.
///     </para>
/// </remarks>
public sealed class StudentActivationRoutingTests
{
    private static string[] SliceFiles()
    {
        string sliceDirectory = Path.Combine(RepositoryFiles.Root().FullName, "src", "features", "Students");

        return Directory.GetFiles(sliceDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    ///     A guard on the guard: a scan of a directory that moved or emptied passes silently.
    /// </summary>
    [Fact]
    public void SliceFiles_AreActuallyBeingInspected()
    {
        Assert.Equal(5, SliceFiles().Length);
    }

    /// <summary>
    ///     Assignment <em>through a receiver</em> — <c>student.IsActive = …</c> — is what is
    ///     prohibited.
    /// </summary>
    /// <remarks>
    ///     Not a bare <c>IsActive =</c> search. The response projection legitimately writes
    ///     <c>IsActive = student.IsActive</c> in an object initialiser, and a scan that flagged it
    ///     would have to be deleted or weakened the first time it ran — which is how a check stops
    ///     being a check. The negative lookahead excludes the comparison <c>==</c>.
    /// </remarks>
    [Fact]
    public void Slices_NeverAssignIsActiveDirectly()
    {
        Regex assignment = new(@"[A-Za-z_][A-Za-z0-9_]*\.IsActive\s*=(?!=)", RegexOptions.None, TimeSpan.FromSeconds(2));

        foreach (string file in SliceFiles())
        {
            string source = File.ReadAllText(file);

            Assert.False(
                assignment.IsMatch(source),
                $"{Path.GetFileName(file)} assigns IsActive directly. Route the change through "
                + "ActivationPolicy.Apply / ApplyReplacement with ActivationPrivilege.SchoolScope "
                + "(DEC-20, O-12) — the privilege attaches to the transition, not to the endpoint.");
        }
    }

    /// <summary>
    ///     The pattern above matches the statement it is meant to prohibit, and not the initialiser it
    ///     is not. Without this, weakening the regex to nothing would leave the sweep green.
    /// </summary>
    [Theory]
    [InlineData("student.IsActive = false;", true)]
    [InlineData("entity.IsActive = requestedIsActive;", true)]
    [InlineData("IsActive = student.IsActive,", false)]
    [InlineData("if (entity.IsActive == requestedIsActive)", false)]
    [InlineData("students.Where(student => student.IsActive)", false)]
    public void AssignmentPattern_MatchesOnlyReceiverAssignment(string line, bool expected)
    {
        Regex assignment = new(@"[A-Za-z_][A-Za-z0-9_]*\.IsActive\s*=(?!=)", RegexOptions.None, TimeSpan.FromSeconds(2));

        Assert.Equal(expected, assignment.IsMatch(line));
    }

    /// <summary>
    ///     The two slices that change activation state both name their privilege class, and both name
    ///     the same one: DEC-20 requires school scope, and no more, to deactivate a <c>Student</c>.
    /// </summary>
    [Theory]
    [InlineData("UpdateStudent.cs", "ActivationPolicy.ApplyReplacement")]
    [InlineData("DeactivateStudent.cs", "ActivationPolicy.Apply")]
    public void ActivationSlices_RouteThroughThePolicyWithSchoolScope(string fileName, string expectedCall)
    {
        string source = File.ReadAllText(
            Path.Combine(RepositoryFiles.Root().FullName, "src", "features", "Students", fileName));

        Assert.Contains(expectedCall, source, StringComparison.Ordinal);
        Assert.Contains("ActivationPrivilege.SchoolScope", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ActivationPrivilege.SystemAdmin", source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     F05 produces no 403 on any route (spec §6), so no slice may raise one — and none may declare
    ///     one either, because documenting a status the routes cannot produce is as wrong as omitting
    ///     one they can.
    /// </summary>
    [Fact]
    public void Slices_DeclareAndThrowNoForbidden()
    {
        foreach (string file in SliceFiles())
        {
            string source = File.ReadAllText(file);

            Assert.DoesNotContain("ForbiddenException", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Status403Forbidden", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Status409Conflict", source, StringComparison.Ordinal);
        }
    }
}
