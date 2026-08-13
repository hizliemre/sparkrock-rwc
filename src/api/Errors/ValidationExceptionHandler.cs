using api.Errors;
using domain.Exceptions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace api;

/// <summary>
///     Turns a FluentValidation failure into the shared error envelope.
/// </summary>
/// <remarks>
///     Writes a plain <see cref="ProblemDetails" /> with <c>violations</c> in
///     <see cref="ProblemDetails.Extensions" />, never <c>ValidationProblemDetails</c>. That type
///     serialises its errors as a JSON <em>object</em> at the same pointer the envelope uses for an
///     <em>array</em>, so mixing the two would put two shapes behind one key — worse than the
///     original defect, which was merely that the error code never reached the client at all.
/// </remarks>
internal sealed class ValidationExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not ValidationException validationException)
            return false;

        List<object> violations = validationException.Errors
            .Select(failure => (object)new
            {
                source = "body",
                path = ViolationPath.ToCamelCase(failure.PropertyName),
                code = CodeFor(failure),
                message = failure.ErrorMessage
            })
            .ToList();

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

        ProblemDetails problemDetails = new()
        {
            Status = StatusCodes.Status400BadRequest,
            Extensions =
            {
                ["errorCode"] = ErrorCodes.Validation.Failed,
                ["violations"] = violations
            }
        };

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
            Exception = exception
        });
    }

    // A rule with no explicit code still has to carry one; the client branches on it.
    private static string CodeFor(ValidationFailure failure) =>
        string.IsNullOrWhiteSpace(failure.ErrorCode) ? ErrorCodes.Validation.Failed : failure.ErrorCode;
}
