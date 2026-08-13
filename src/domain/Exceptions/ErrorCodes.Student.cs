namespace domain.Exceptions;

/// <summary>
///     Codes for the <c>STUDENT</c> area.
/// </summary>
/// <remarks>
///     A file rather than a line in a shared one (conventions §5), so twelve workstreams adding codes
///     never meet in the same place.
///     <para>
///         One code only, and it carries no message. A student addressed through another school's
///         path and a student that does not exist raise <see cref="NotFoundException" /> with this
///         code from the same query — the two payloads are identical by construction rather than by
///         call-site discipline (spec §4).
///     </para>
///     <para>
///         <c>STUDENT.REFERENCE_MISSING</c>, the foreign-key translation registry's row, belongs to
///         the persistence layer's constraint mapping and is not declared here by F05; F05 turns a
///         missing school into a 404 before any insert, so it is unreachable through this feature.
///     </para>
/// </remarks>
public static partial class ErrorCodes
{
    public static class Student
    {
        public const string NotFound = "STUDENT.NOT_FOUND";
    }
}
