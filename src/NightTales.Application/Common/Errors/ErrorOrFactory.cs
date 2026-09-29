using System.Linq.Expressions;
using ErrorOr;

namespace NightTales.Application.Common.Errors;

/// <summary>
/// Cached, compiled conversion from a list of errors to the response type of a handler, so that a
/// behaviour constrained to <c>where TResponse : IErrorOr</c> can short-circuit without reflection
/// per call (09 README D4).
/// </summary>
/// <typeparam name="TResponse">The handler's response type; must be an <see cref="ErrorOr{TValue}"/>.</typeparam>
#pragma warning disable CA1000 // The behaviour contract is ErrorOrFactory<TResponse>.From(errors); the type parameter is the only way to type it.
public static class ErrorOrFactory<TResponse>
    where TResponse : IErrorOr
{
    private static readonly Func<List<Error>, TResponse> Converter = Compile();

    /// <summary>Builds a <typeparamref name="TResponse"/> carrying exactly <paramref name="errors"/>. Must be non-empty: an empty list would otherwise convert to a successful default response.</summary>
    public static TResponse From(List<Error> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentOutOfRangeException.ThrowIfZero(errors.Count);
        return Converter(errors);
    }

    private static Func<List<Error>, TResponse> Compile()
    {
        var errors = Expression.Parameter(typeof(List<Error>), "errors");
        try
        {
            // Expression.Convert resolves ErrorOr<TValue>'s implicit operator from List<Error>.
            return Expression
                .Lambda<Func<List<Error>, TResponse>>(Expression.Convert(errors, typeof(TResponse)), errors)
                .Compile();
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"No conversion from List<Error> to {typeof(TResponse)}; a pipeline response type must be ErrorOr<T>.",
                exception);
        }
    }
}
#pragma warning restore CA1000
