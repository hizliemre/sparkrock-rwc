using domain.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace api.Errors;

/// <summary>
///     Maps the domain exception vocabulary onto the status contract.
/// </summary>
/// <remarks>
///     404 for tenancy, 403 for privilege. A resource outside the caller's scope is 404 with a
///     payload identical to genuine not-found, because a distinguishable status confirms the record
///     exists. A resource the caller can legitimately see but is not privileged to change is 403 —
///     returning 404 there would contradict the 200 on the same id and hide nothing.
///     <para>
///         <c>violations</c> appears only for per-item failures. On 403, 404 and 409 there is no item
///         to point at, and an empty array would imply otherwise.
///     </para>
///     <para>
///         The write goes through <see cref="ProblemDetailsEnvelope" />, never
///         <c>TryWriteAsync</c> directly: a declined content negotiation must still be reported as
///         handled, or the developer exception page serves the stack trace instead.
///     </para>
/// </remarks>
internal sealed class DomainExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        (int status, string errorCode, string? detail, List<object>? violations) = exception switch
        {
            BusinessRuleException business => (
                StatusCodes.Status400BadRequest,
                business.ErrorCode,
                (string?)null,
                ToViolations(business.Violations)),

            ForbiddenException forbidden => (
                StatusCodes.Status403Forbidden, forbidden.ErrorCode, forbidden.Message, (List<object>?)null),

            NotFoundException notFound => (
                StatusCodes.Status404NotFound, notFound.ErrorCode, notFound.Message, (List<object>?)null),

            // Retryable and permanent conflicts share a status but keep distinct codes and,
            // deliberately, distinct types: the save pipeline's retry must not catch both.
            ConcurrencyConflictException concurrency => (
                StatusCodes.Status409Conflict, concurrency.ErrorCode, concurrency.Message, (List<object>?)null),

            ConflictException conflict => (
                StatusCodes.Status409Conflict, conflict.ErrorCode, conflict.Message, (List<object>?)null),

            _ => (0, string.Empty, null, null)
        };

        if (status == 0)
            return false;

        httpContext.Response.StatusCode = status;

        ProblemDetails problemDetails = new()
        {
            Status = status,
            // ForbiddenException and ConflictException take an arbitrary message and it lands here
            // verbatim, so Detail is a free-text channel too. There is no property path to key the
            // field-name rule on, so only the length cap applies — enough to stop an interpolated
            // Notes column, not enough to make the channel safe. Recorded as O-46.
            Detail = detail is null ? null : ViolationMessage.Sanitise(detail, clrPath: null, attemptedValue: null),
            Extensions = { ["errorCode"] = errorCode }
        };

        if (violations is not null)
            problemDetails.Extensions["violations"] = violations;

        return await ProblemDetailsEnvelope.WriteAsync(problemDetailsService, new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
            Exception = exception
        });
    }

    /// <summary>
    ///     Projects domain violations onto the wire shape.
    /// </summary>
    /// <remarks>
    ///     <c>Source</c> comes from the handler here rather than being inferred: a
    ///     <see cref="BusinessRuleException" /> is raised about a payload the handler has already
    ///     parsed, so it knows where each item came from. Messages still pass through
    ///     <see cref="ViolationMessage" /> — nothing stops a handler interpolating the value it
    ///     rejected, and conventions §2 forbids free text in a response body regardless of who wrote
    ///     the string.
    /// </remarks>
    private static List<object> ToViolations(IReadOnlyList<Violation> violations) =>
        violations
            .Select(violation => (object)new
            {
                source = violation.Source,
                path = ViolationPath.ToCamelCase(violation.Path),
                code = violation.Code,
                message = ViolationMessage.Sanitise(violation.Message, violation.Path, attemptedValue: null)
            })
            .ToList();
}
