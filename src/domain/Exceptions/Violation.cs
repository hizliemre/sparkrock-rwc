namespace domain.Exceptions;

/// <summary>
///     One per-item failure in an error response (conventions §2).
/// </summary>
/// <param name="Source">Where the offending value came from: <c>body</c>, <c>path</c>, <c>query</c> or <c>header</c>.</param>
/// <param name="Path">
///     The CLR property path, e.g. <c>Entries[3].AttendCode</c>. The API layer camel-cases it per
///     segment; handlers never do.
/// </param>
/// <param name="Code">The <c>AREA.CONDITION</c> code the client branches on.</param>
/// <param name="Message">
///     Server-side English, a developer aid. Never echoes a free-text field — <c>Notes</c> routinely
///     carries health and safeguarding detail.
/// </param>
public sealed record Violation(string Source, string Path, string Code, string Message);
