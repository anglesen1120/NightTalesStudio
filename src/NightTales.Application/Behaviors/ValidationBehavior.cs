using System.Text.Json;
using ErrorOr;
using FluentValidation;
using Mediator;
using NightTales.Application.Common.Errors;

namespace NightTales.Application.Behaviors;

/// <summary>
/// Runs every registered <see cref="IValidator{T}"/> for the message and short-circuits the pipeline
/// with one <c>validation_failed</c> error per failure, carrying the camelCase field name in metadata
/// (09 README D7). The handler is never invoked when validation fails. An exception thrown by a
/// validator propagates: it is a bug, not a validation error.
/// </summary>
public sealed class ValidationBehavior<TMessage, TResponse>(IEnumerable<IValidator<TMessage>> validators)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
    where TResponse : IErrorOr
{
    private readonly IValidator<TMessage>[] _validators = validators.ToArray();

    public async ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        if (_validators.Length == 0)
        {
            return await next(message, cancellationToken).ConfigureAwait(false);
        }

        var context = new ValidationContext<TMessage>(message);
        List<Error>? failures = null;

        foreach (var validator in _validators)
        {
            var result = await validator.ValidateAsync(context, cancellationToken).ConfigureAwait(false);
            foreach (var failure in result.Errors)
            {
                failures ??= [];
                failures.Add(Error.Validation(
                    code: ErrorCodes.ValidationFailed,
                    description: failure.ErrorMessage,
                    metadata: new Dictionary<string, object>
                    {
                        [ErrorMetadata.Field] = ToCamelCase(failure.PropertyName),
                    }));
            }
        }

        if (failures is not null)
        {
            return ErrorOrFactory<TResponse>.From(failures);
        }

        return await next(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Camel-cases every segment of a FluentValidation property path (<c>Address.Street</c> → <c>address.street</c>).</summary>
    private static string ToCamelCase(string propertyName)
    {
        var segments = propertyName.Split('.');
        for (var i = 0; i < segments.Length; i++)
        {
            segments[i] = JsonNamingPolicy.CamelCase.ConvertName(segments[i]);
        }

        return string.Join('.', segments);
    }
}
