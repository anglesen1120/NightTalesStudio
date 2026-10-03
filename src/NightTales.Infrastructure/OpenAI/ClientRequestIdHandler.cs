using NightTales.Application.Abstractions.Text;

namespace NightTales.Infrastructure.OpenAI;

/// <summary>
/// Sets <c>X-Client-Request-Id</c> from the ambient <see cref="TextCallScope"/> (02 README D7): the SDK's
/// request options carry no headers, so the correlation id rides on the named <c>"openai"</c>
/// <c>HttpClient</c>. Outside a scope no header is set.
/// </summary>
public sealed class ClientRequestIdHandler : DelegatingHandler
{
    /// <summary>The header name.</summary>
    public const string HeaderName = "X-Client-Request-Id";

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (TextCallScope.Current is { } id)
        {
            request.Headers.Remove(HeaderName);
            request.Headers.TryAddWithoutValidation(HeaderName, id);
        }

        // The send is the moment a billed generation becomes possible; a token cancelled before it sends nothing.
        cancellationToken.ThrowIfCancellationRequested();
        TextCallScope.MarkDispatched();

        return base.SendAsync(request, cancellationToken);
    }
}
