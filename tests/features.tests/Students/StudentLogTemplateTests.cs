using System.Reflection;
using features.Students;
using Microsoft.Extensions.Logging;

namespace features.tests.Students;

/// <summary>
///     Conventions §4's "no PII in any log template", applied to the one feature it was written for.
/// </summary>
/// <remarks>
///     Conventions §4 marks the rule ⚙ and describes a test that inspects <c>[LoggerMessage]</c>
///     templates for banned field names. <b>No such test exists in this repository</b> — the mechanism
///     is prose, not a mechanism (reported as an F05 finding). This file is F05's own copy, scoped to
///     the Students slices, so the feature that handles children's names is not relying on a test that
///     was specified and never written.
///     <para>
///         The vacuity guard matters more than the assertion. A reflective sweep that finds no
///         templates passes silently, which is exactly the defect class this codebase keeps
///         reproducing — so the count of inspected templates is asserted first.
///     </para>
/// </remarks>
public sealed class StudentLogTemplateTests
{
    /// <summary>
    ///     Every write slice in F05 logs once, after <c>SaveChangesAsync</c>. Query handlers log
    ///     nothing (conventions §4), so three is the whole set.
    /// </summary>
    private const int ExpectedTemplateCount = 3;

    private static readonly string[] BannedFragments =
    [
        "FirstName", "LastName", "Grade", "Name", "Notes", "DateOfBirth", "Legacy"
    ];

    private static (string Method, string Template)[] Templates()
    {
        return typeof(CreateStudent).Assembly
            .GetTypes()
            .Where(type => type.Namespace == typeof(CreateStudent).Namespace)
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
                | BindingFlags.DeclaredOnly))
            .Select(method => (Method: method, Attribute: method.GetCustomAttribute<LoggerMessageAttribute>()))
            .Where(candidate => candidate.Attribute is not null)
            .Select(candidate => (
                Method: FormattableString.Invariant($"{candidate.Method.DeclaringType!.Name}.{candidate.Method.Name}"),
                Template: candidate.Attribute!.Message))
            .ToArray();
    }

    /// <summary>
    ///     A guard on the guard. Without it, deleting every log line — or renaming the namespace —
    ///     leaves the sweep below asserting over an empty collection and passing.
    /// </summary>
    [Fact]
    public void LogTemplates_AreActuallyBeingInspected()
    {
        Assert.Equal(ExpectedTemplateCount, Templates().Length);
    }

    [Fact]
    public void LogTemplates_ContainNoStudentAttribute()
    {
        foreach ((string method, string template) in Templates())
        {
            foreach (string banned in BannedFragments)
            {
                Assert.False(
                    template.Contains(banned, StringComparison.OrdinalIgnoreCase),
                    $"{method} logs '{template}', which names '{banned}'. Conventions §4 permits ids and "
                    + "counts only; a name in a log line survives log retention and ships to every "
                    + "aggregator.");
            }
        }
    }

    /// <summary>
    ///     `EventId`s 1200–1299 are allocated to Students (conventions §4) and are never reused.
    /// </summary>
    [Fact]
    public void LogEventIds_AreInTheStudentsRange()
    {
        int[] eventIds = typeof(CreateStudent).Assembly
            .GetTypes()
            .Where(type => type.Namespace == typeof(CreateStudent).Namespace)
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
                | BindingFlags.DeclaredOnly))
            .Select(method => method.GetCustomAttribute<LoggerMessageAttribute>())
            .Where(attribute => attribute is not null)
            .Select(attribute => attribute!.EventId)
            .ToArray();

        Assert.Equal(ExpectedTemplateCount, eventIds.Length);
        Assert.Equal(eventIds.Length, eventIds.Distinct().Count());
        Assert.All(eventIds, eventId => Assert.InRange(eventId, 1200, 1299));
    }
}
