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
/// </remarks>
internal sealed class DomainExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
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
            Detail = detail,
            Extensions = { ["errorCode"] = errorCode }
        };

        if (violations is not null)
            problemDetails.Extensions["violations"] = violations;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
            Exception = exception
        });
    }

    private static List<object> ToViolations(IReadOnlyList<Violation> violations) =>
        violations
            .Select(violation => (object)new
            {
                source = violation.Source,
                path = ViolationPath.ToCamelCase(violation.Path),
                code = violation.Code,
                message = violation.Message
            })
            .ToList();
}
