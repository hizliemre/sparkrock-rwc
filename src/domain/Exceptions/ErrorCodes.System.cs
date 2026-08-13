namespace domain.Exceptions;

/// <summary>
///     Codes stamped on responses that never reach an exception handler, plus the privilege failure.
/// </summary>
/// <remarks>
///     Naming hazard: the nested <c>System</c> class shadows the <c>System</c> namespace inside this
///     declaration. A future area file must not write unqualified <c>System.Something</c> within the
///     partial. The class name is not negotiable — conventions §5 names the area <c>SYSTEM</c>.
/// </remarks>
public static partial class ErrorCodes
{
    public static class System
    {
        public const string MalformedRequest = "SYSTEM.MALFORMED_REQUEST";

        public const string NotFound = "SYSTEM.NOT_FOUND";

        public const string MethodNotAllowed = "SYSTEM.METHOD_NOT_ALLOWED";

        public const string UnsupportedMediaType = "SYSTEM.UNSUPPORTED_MEDIA_TYPE";

        public const string Forbidden = "SYSTEM.FORBIDDEN";

        public const string Unexpected = "SYSTEM.UNEXPECTED";
    }
}
