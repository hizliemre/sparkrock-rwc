namespace domain.Exceptions;

/// <summary>
///     Codes for the <c>TERM</c> area.
/// </summary>
/// <remarks>
///     A file rather than a line in a shared one (conventions §5), so twelve workstreams adding codes
///     never meet in the same place.
///     <para>
///         F04's spec §7 expects this file to have arrived with F01c carrying a
///         <c>TERM.REFERENCE_MISSING</c> constant for <c>fk_school_terms_schools_school_id</c>. It did
///         not, and the constant is deliberately still absent: <c>SchemaConstraintErrors</c> maps no
///         foreign key at all — "they are all <c>Restrict</c>, a violation means a bug rather than a
///         race" — so the constant would be referenced by nothing. An unused code is the inert
///         mechanism this codebase keeps finding; the feature that adds the registry row adds the
///         constant with it.
///     </para>
/// </remarks>
public static partial class ErrorCodes
{
    public static class Term
    {
        /// <summary>
        ///     No such term, or the term belongs to a school other than the one in the path.
        /// </summary>
        /// <remarks>
        ///     One code for both, because <see cref="NotFoundException" /> takes no message: a term of
        ///     another school and an absent term produce the identical payload by construction. The
        ///     school-level check has already established that the caller may see this school, so a
        ///     term-specific code discloses nothing.
        /// </remarks>
        public const string NotFound = "TERM.NOT_FOUND";

        /// <summary>
        ///     The requested dates share at least one day with another active term of the same school
        ///     (V-19).
        /// </summary>
        /// <remarks>
        ///     Bounds are <b>closed</b>, so a term ending on the day another begins is an overlap.
        ///     Application-enforced only — there is no exclusion constraint — so two simultaneous
        ///     writers can still commit an overlapping pair. Recovery is
        ///     <c>PUT { "isActive": false }</c> on one of them.
        /// </remarks>
        public const string Overlap = "TERM.OVERLAP";
    }
}
