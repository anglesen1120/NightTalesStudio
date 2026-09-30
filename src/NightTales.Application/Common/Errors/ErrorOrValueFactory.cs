using System.Linq.Expressions;
using ErrorOr;

namespace NightTales.Application.Common.Errors;

/// <summary>
/// Cached, compiled conversion from a stored success value back to the response type of a handler: the
/// symmetric half of <see cref="ErrorOrFactory{TResponse}"/>, so a behaviour that replays an
/// idempotent response can rebuild <typeparamref name="TResponse"/> without reflection per call
/// (09 README D6).
/// </summary>
/// <typeparam name="TResponse">The handler's response type; must be an <see cref="ErrorOr{TValue}"/>.</typeparam>
#pragma warning disable CA1000 // The contract is ErrorOrValueFactory<TResponse>.From(value); the type parameter is the only way to type it.
public static class ErrorOrValueFactory<TResponse>
    where TResponse : IErrorOr
{
    /// <summary>
    /// The success value type behind <typeparamref name="TResponse"/> — <c>Guid</c> for
    /// <c>ErrorOr&lt;Guid&gt;</c>. Both the serializer and the replay path need it: the stored value is
    /// serialized and deserialized as this type, never as the boxed <c>ErrorOr</c>.
    /// </summary>
    public static Type ValueType { get; } = ResolveValueType();

    private static readonly Func<object, TResponse> Converter = Compile();

    private static readonly Func<TResponse, object?> Reader = CompileReader();

    /// <summary>
    /// The success value of <paramref name="response"/>, boxed. The non-generic <see cref="IErrorOr"/> has
    /// no <c>Value</c>, so the getter is compiled once per response type from
    /// <see cref="IErrorOr{TValue}"/> — never reflected per call.
    /// </summary>
    /// <param name="response">A successful response; an error response has no value.</param>
    public static object? ValueOf(TResponse response) => Reader(response);

    /// <summary>Wraps a deserialized success value of <see cref="ValueType"/> as <typeparamref name="TResponse"/>.</summary>
    /// <param name="value">A non-null instance of <see cref="ValueType"/>.</param>
    public static TResponse From(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!ValueType.IsInstanceOfType(value))
        {
            throw new ArgumentException(
                $"Value of type '{value.GetType()}' is not the success value type '{ValueType}' of '{typeof(TResponse)}'.",
                nameof(value));
        }

        return Converter(value);
    }

    private static Type ResolveValueType()
    {
        foreach (var implemented in typeof(TResponse).GetInterfaces())
        {
            if (implemented.IsGenericType && implemented.GetGenericTypeDefinition() == typeof(IErrorOr<>))
            {
                return implemented.GetGenericArguments()[0];
            }
        }

        throw new InvalidOperationException(
            $"'{typeof(TResponse)}' does not implement IErrorOr<TValue>; a pipeline response type must be ErrorOr<T>.");
    }

    private static Func<object, TResponse> Compile()
    {
        var value = Expression.Parameter(typeof(object), "value");
        try
        {
            // object → the success value type (unbox, then the deserializer's own type), then the
            // implicit operator ErrorOr<TValue>(TValue) that Expression.Convert resolves.
            var converted = Expression.Convert(Expression.Convert(value, ValueType), typeof(TResponse));
            return Expression.Lambda<Func<object, TResponse>>(converted, value).Compile();
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"No conversion from '{ValueType}' to {typeof(TResponse)}; a pipeline response type must be ErrorOr<T>.",
                exception);
        }
    }

    private static Func<TResponse, object?> CompileReader()
    {
        var response = Expression.Parameter(typeof(TResponse), "response");
        try
        {
            // TResponse : IErrorOr (a constraint only): Value lives on IErrorOr<TValue>, so the getter
            // goes through that interface. The conversion boxes a struct response and never fails at run
            // time, because the constraint plus the resolved ValueType make the cast exact.
            var asValue = Expression.Convert(response, typeof(IErrorOr<>).MakeGenericType(ValueType));
            var value = Expression.Property(asValue, nameof(IErrorOr<Success>.Value));

            return Expression.Lambda<Func<TResponse, object?>>(Expression.Convert(value, typeof(object)), response).Compile();
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"No readable value on '{typeof(TResponse)}'; a pipeline response type must be ErrorOr<T>.",
                exception);
        }
    }
}
#pragma warning restore CA1000
