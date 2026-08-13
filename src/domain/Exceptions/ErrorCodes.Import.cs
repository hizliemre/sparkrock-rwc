namespace domain.Exceptions;

/// <summary>
///     Codes for the <c>IMPORT</c> area.
/// </summary>
/// <remarks>
///     A file rather than a line in a shared one (conventions §5), so twelve workstreams adding codes
///     never meet in the same place.
/// </remarks>
public static partial class ErrorCodes
{
    public static class Import
    {
        /// <summary>
        ///     A row carrying this legacy identifier has already been imported.
        /// </summary>
        /// <remarks>
        ///     The unique filtered <c>legacy_id</c> indexes are what make a re-run of the importer
        ///     safe (DEC-02). Without them a second pass duplicates every row and silently doubles
        ///     every absence recount, which is why the index is unique rather than merely an index.
        ///     Not retryable: the same source row produces the same key on every attempt.
        /// </remarks>
        public const string DuplicateLegacyId = "IMPORT.DUPLICATE_LEGACY_ID";
    }
}
