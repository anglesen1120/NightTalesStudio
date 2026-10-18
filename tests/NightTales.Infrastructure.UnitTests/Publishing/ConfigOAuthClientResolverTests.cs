using ErrorOr;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Publishing;
using NightTales.Infrastructure.Publishing;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Publishing;

/// <summary>
/// The OAuth client resolver of 06.1.2-T1 (06 README D5): it maps a Google client id to the client record of the
/// configured map and resolves that record's secret through <see cref="ISecretProvider"/> at the moment of use —
/// an unknown id never reaches the secret port, and a failure never carries the reference or a value.
/// </summary>
public sealed class ConfigOAuthClientResolverTests
{
    private const string ClientId = "nts-youtube-dev";
    private const string SecretRef = "nts/dev/api/YOUTUBE_CLIENT_SECRET";
    private const string SecretCanary = "canary-secret-aaaa";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void ActiveClientId_is_the_configured_one()
    {
        Resolver(Serving(SecretCanary)).ActiveClientId.ShouldBe(ClientId);
    }

    [Fact]
    public async Task A_configured_client_resolves_with_its_secret()
    {
        var client = await Resolver(Serving(SecretCanary)).ResolveAsync(ClientId, Ct);

        client.IsError.ShouldBeFalse();
        client.Value.ClientId.ShouldBe(ClientId);
        client.Value.ClientSecret.ShouldBe(SecretCanary);
    }

    [Fact]
    public async Task The_configured_reference_is_what_the_secret_port_is_asked_for()
    {
        var secrets = new RecordingSecretProvider(SecretCanary);

        await Resolver(secrets).ResolveAsync(ClientId, Ct);

        secrets.Calls.ShouldBe(1);
        secrets.LastReference.ShouldBe(SecretRef);
    }

    [Fact]
    public async Task An_unknown_client_is_not_found_and_never_reaches_the_secret_port()
    {
        var secrets = new RecordingSecretProvider(SecretCanary);

        var client = await Resolver(secrets).ResolveAsync("nts-some-other-client", Ct);

        client.IsError.ShouldBeTrue();
        client.FirstError.Code.ShouldBe("oauth_client_unknown");
        client.FirstError.Type.ShouldBe(ErrorType.NotFound);
        secrets.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_client_id_is_unknown(string oauthClientId)
    {
        var secrets = new RecordingSecretProvider(SecretCanary);

        var client = await Resolver(secrets).ResolveAsync(oauthClientId, Ct);

        client.IsError.ShouldBeTrue();
        client.FirstError.Code.ShouldBe("oauth_client_unknown");
        secrets.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task An_entry_without_a_reference_is_unknown()
    {
        var secrets = new RecordingSecretProvider(SecretCanary);

        var client = await Resolver(secrets, secretRef: "  ").ResolveAsync(ClientId, Ct);

        client.IsError.ShouldBeTrue();
        client.FirstError.Code.ShouldBe("oauth_client_unknown");
        secrets.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_secret_the_provider_cannot_answer_is_unavailable()
    {
        var client = await Resolver(Serving(Error.Unexpected("secret_unavailable"))).ResolveAsync(ClientId, Ct);

        client.IsError.ShouldBeTrue();
        client.FirstError.Code.ShouldBe("oauth_client_secret_unavailable");
        client.FirstError.Type.ShouldBe(ErrorType.Unexpected);
        client.FirstError.Description.ShouldNotContain(SecretRef);
        client.FirstError.Description.ShouldNotContain(SecretCanary);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_secret_value_is_unavailable(string value)
    {
        var client = await Resolver(Serving(value)).ResolveAsync(ClientId, Ct);

        client.IsError.ShouldBeTrue();
        client.FirstError.Code.ShouldBe("oauth_client_secret_unavailable");
    }

    private static ConfigOAuthClientResolver Resolver(ISecretProvider secrets, string secretRef = SecretRef) =>
        new(
            Options.Create(new YouTubeOptions
            {
                ActiveClientId = ClientId,
                Clients = new Dictionary<string, YouTubeClientOptions>(StringComparer.Ordinal)
                {
                    [ClientId] = new YouTubeClientOptions { SecretRef = secretRef },
                },
            }),
            secrets);

    private static RecordingSecretProvider Serving(ErrorOr<string> answer) => new(answer);

    /// <summary>A hand-written <see cref="ISecretProvider"/> that answers one fixed result and counts the calls.</summary>
    private sealed class RecordingSecretProvider(ErrorOr<string> answer) : ISecretProvider
    {
        public int Calls { get; private set; }

        public string? LastReference { get; private set; }

        public Task<ErrorOr<string>> GetAsync(string secretRef, CancellationToken ct)
        {
            Calls++;
            LastReference = secretRef;
            return Task.FromResult(answer);
        }
    }
}
