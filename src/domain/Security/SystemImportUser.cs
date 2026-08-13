namespace domain.Security;

/// <summary>
///     The reserved identity the importer writes under.
/// </summary>
/// <remarks>
///     Distinct from <see cref="Guid.Empty" />, which the anonymous stub used, so that imported rows
///     stay separable from rows written by an unauthenticated request.
/// </remarks>
public static class SystemImportUser
{
    public static readonly Guid Id = new("00000000-0000-0000-0000-0000000000FF");

    public const string DisplayName = "System Import";

    public static ICurrentUser AsCurrentUser() => new ImportIdentity();

    private sealed class ImportIdentity : ICurrentUser
    {
        public Guid UserId => Id;

        public string DisplayName => SystemImportUser.DisplayName;

        public IReadOnlyCollection<Guid> AuthorizedSchoolIds => [];

        public bool IsSystemAdmin => true;
    }
}
