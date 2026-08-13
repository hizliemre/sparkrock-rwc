namespace domain.Abstraction;

/// <summary>
///     A reference entity whose lifecycle is activation rather than deletion.
/// </summary>
/// <remarks>
///     <c>School</c>, <c>Student</c>, <c>AttendanceCode</c> and <c>SchoolTerm</c> each already declare
///     exactly this property; the interface exists so the one place the DEC-20 privilege rule lives —
///     <see cref="domain.Security.ActivationPolicy" /> — can be written once instead of four times.
///     <para>
///         The setter is public, unlike <see cref="ISchoolScoped" />'s get-only scope key, because the
///         policy is what assigns it. Nothing mechanically stops a handler assigning
///         <c>IsActive</c> directly; that is a convention backed by the per-slice tests, and the
///         variant that would be a guarantee (a private setter plus a mutator taking
///         <c>ICurrentUser</c>) was rejected because the legacy import legitimately constructs
///         inactive rows under a system identity.
///     </para>
/// </remarks>
public interface IActivatable
{
    bool IsActive { get; set; }
}
