using domain.Exceptions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace api.Errors;

/// <summary>
///     Turns a FluentValidation failure into the shared error envelope.
/// </summary>
/// <remarks>
///     Writes a plain <see cref="ProblemDetails" /> with <c>violations</c> in
///     <see cref="ProblemDetails.Extensions" />, never <c>ValidationProblemDetails</c>. That type
///     serialises its errors as a JSON <em>object</em> at the same pointer the envelope uses for an
///     <em>array</em>, so mixing the two would put two shapes behind one key — worse than the
///     original defect, which was merely that the error code never reached the client at all.
///     <para>
///         Two things about a violation are not the validator's to state. <c>source</c> is inferred
///         from the request by <see cref="ViolationSource" />, because a validator knows the property
///         name and nothing about where it was bound from; and <c>message</c> passes through
///         <see cref="ViolationMessage" />, because FluentValidation's built-in messages interpolate
///         <c>{PropertyValue}</c> and conventions §2 forbids free text in a response body.
///     </para>
/// </remarks>
internal sealed class ValidationExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (exception is not ValidationException validationException)
            return false;

        List<object> violations = validationException.Errors
            .Select(failure => (object)new
            {
                source = ViolationSource.For(httpContext.Request, failure.PropertyName),
                path = ViolationPath.ToCamelCase(failure.PropertyName),
                code = CodeFor(failure),
                message = ViolationMessage.Sanitise(
                    failure.ErrorMessage, failure.PropertyName, failure.AttemptedValue)
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

        return await ProblemDetailsEnvelope.WriteAsync(problemDetailsService, new ProblemDetailsContext
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
