using System.Reflection;
using features.Alerts;
using Microsoft.Extensions.Logging;

namespace features.tests.Alerts;

/// <summary>
///     Acceptance criterion 15 — conventions §4's "no PII in any log template", applied to
///     <c>features/Alerts/</c>.
/// </summary>
/// <remarks>
///     Conventions §4 marks the rule ⚙ and describes a repository-wide test that inspects
///     <c>[LoggerMessage]</c> templates. <b>No such test exists</b>; F05 wrote its own local copy for
///     the same reason and reported it. This is F10's, because a resolution reason is free text
///     written by a person about a safeguarding decision and is the single worst thing in this
///     aggregate to put in a log line.
///     <para>
///         The vacuity guard matters more than the assertion: a reflective sweep that finds no
///         templates passes silently, which is the defect class this codebase keeps reproducing.
///     </para>
/// </remarks>
public sealed class AlertLogTemplateTests
{
    private static readonly string[] BannedFragments =
    [
        "Student", "FirstName", "LastName", "Name", "Reason", "Notes", "Grade", "DateOfBirth", "Legacy"
    ];

    private static (string Method, string Template)[] Templates() =>
        typeof(GetSchoolAlerts).Assembly
            .GetTypes()
            .Where(type => type.Namespace == typeof(GetSchoolAlerts).Namespace)
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
                | BindingFlags.DeclaredOnly))
            .Select(method => (Method: method, Attribute: method.GetCustomAttribute<LoggerMessageAttribute>()))
            .Where(candidate => candidate.Attribute is not null)
            .Select(candidate => (
                Method: FormattableString.Invariant($"{candidate.Method.DeclaringType!.Name}.{candidate.Method.Name}"),
                Template: candidate.Attribute!.Message))
            .ToArray();

    /// <summary>
    ///     A guard on the guard. Without it, deleting the log line — or renaming the namespace —
    ///     leaves the sweep below asserting over an empty collection and passing.
    /// </summary>
    /// <remarks>
    ///     Exactly one: <c>ResolveAlert</c> logs once after the save, and <c>GetSchoolAlerts</c> is a
    ///     query handler, which logs nothing (conventions §4).
    /// </remarks>
    [Fact]
    public void LogTemplates_AreActuallyBeingInspected() => Assert.Single(Templates());

    [Fact]
    public void LogTemplates_NameNoStudentNameOrReason()
    {
        foreach ((string method, string template) in Templates())
        {
            foreach (string banned in BannedFragments)
            {
                Assert.False(
                    template.Contains(banned, StringComparison.OrdinalIgnoreCase),
                    $"{method} logs '{template}', which names '{banned}'. Conventions §4 permits ids "
                    + "and counts only; a resolution reason in a log line survives log retention and "
                    + "ships to every aggregator.");
            }
        }
    }

    /// <summary>EventIds 1600–1699 are allocated to Alerts (conventions §4) and are never reused.</summary>
    [Fact]
    public void LogEventIds_AreInTheAlertsRange()
    {
        int[] eventIds = typeof(GetSchoolAlerts).Assembly
            .GetTypes()
            .Where(type => type.Namespace == typeof(GetSchoolAlerts).Namespace)
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
                | BindingFlags.DeclaredOnly))
            .Select(method => method.GetCustomAttribute<LoggerMessageAttribute>())
            .Where(attribute => attribute is not null)
            .Select(attribute => attribute!.EventId)
            .ToArray();

        Assert.InRange(Assert.Single(eventIds), 1600, 1699);
    }
}
