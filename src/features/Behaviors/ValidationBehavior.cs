using FluentValidation;
using FluentValidation.Results;
using MediatR;

namespace features.Behaviors;

internal sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        IValidator<TRequest>[] requestValidators = validators.ToArray();

        if (requestValidators.Length == 0)
            return await next(cancellationToken);

        ValidationContext<TRequest> context = new(request);

        ValidationResult[] results = await Task.WhenAll(
            requestValidators.Select(validator => validator.ValidateAsync(context, cancellationToken)));

        ValidationFailure[] failures = results
            .SelectMany(result => result.Errors)
            .Where(failure => failure is not null)
            .ToArray();

        if (failures.Length > 0)
            throw new ValidationException(failures);

        return await next(cancellationToken);
    }
}
