using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Application.Abstractions.Publishing;
using NightTales.TestKit.Api;

namespace NightTales.TestKit.Google;

/// <summary>
/// A hand-written <see cref="IOAuthClientResolver"/> for the tests that need a resolver failure: the
/// connect route maps the resolver's two catalogue codes — an unknown client and an unreadable secret —
/// onto <c>404 not_found</c> and <c>503 service_unavailable</c>, and neither Mapping may leak the
/// resolver's own constant (06.1.2-T1 failure handling, <c>PublishingEndpoints.MapConnectFailure</c>).
/// </summary>
/// <remarks>
/// Nothing else is replaced: <see cref="ActiveClientId"/> is what a connect would ask for, and the
/// configured client map stays in place, so only the resolution's answer is the test's.
/// </remarks>
public sealed class FakeOAuthClientResolver : IOAuthClientResolver
{
    private readonly List<string> _calls = [];
    private readonly object _gate = new();

    /// <summary>The id a connect resolves; the active client of the configured map by default.</summary>
    public string ActiveClientId { get; set; } = "canary-oauth-client-primary";

    /// <summary>What the next <c>ResolveAsync</c> answers; an <see cref="Error"/> when a test drives a failure.</summary>
    public ErrorOr<OAuthClient> NextResult { get; set; } = Error.Unexpected("canary-not-configured");

    /// <summary>Every client id the route asked to resolve, in arrival order.</summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>How many resolutions arrived.</summary>
    public int CallCount
    {
        get
        {
            lock (_gate)
            {
                return _calls.Count;
            }
        }
    }

    /// <inheritdoc />
    public Task<ErrorOr<OAuthClient>> ResolveAsync(string oauthClientId, CancellationToken ct)
    {
        lock (_gate)
        {
            _calls.Add(oauthClientId);
        }

        return Task.FromResult(NextResult);
    }
}

/// <summary>The registration seam of <see cref="FakeOAuthClientResolver"/>: the test host only.</summary>
public static class FakeOAuthClientResolverHosting
{
    /// <summary>
    /// Replaces <see cref="IOAuthClientResolver"/> with <paramref name="resolver"/>, so a test can drive
    /// <c>oauth_client_unknown</c> and <c>oauth_client_secret_unavailable</c> without a broken configuration.
    /// </summary>
    /// <param name="factory">The factory to configure.</param>
    /// <param name="resolver">The stand-in.</param>
    public static ApiFactory WithFakeOAuthClientResolver(this ApiFactory factory, FakeOAuthClientResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(resolver);

        return factory.WithServices(services =>
        {
            services.RemoveAll<IOAuthClientResolver>();
            services.AddSingleton<IOAuthClientResolver>(resolver);
        });
    }
}
