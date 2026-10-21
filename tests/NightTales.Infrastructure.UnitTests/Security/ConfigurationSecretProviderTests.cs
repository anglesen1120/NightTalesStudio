using Microsoft.Extensions.Configuration;
using NightTales.Infrastructure.Security;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Security;

/// <summary>
/// The configuration-backed secret provider of 06.1.2-T1 (owner decision 2026-10-18): the platform's
/// <c>nts/&lt;env&gt;/api/YOUTUBE_CLIENT_SECRET</c> reference resolves through its last path segment, so one name
/// works in the secret manager, as an environment variable and in <c>appsettings</c>; everything configuration
/// cannot answer fails with one constant code and a message that carries neither the reference nor a value.
/// </summary>
public sealed class ConfigurationSecretProviderTests
{
    private const string SecretReference = "nts/dev/api/YOUTUBE_CLIENT_SECRET";
    private const string SecretKeyName = "YOUTUBE_CLIENT_SECRET";
    private const string SecretCanary = "canary-secret-aaaa";
    private const string SecretUnavailable = "secret_unavailable";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reference_resolves_through_its_last_path_segment()
    {
        var provider = Provider(new Dictionary<string, string?>
        {
            [SecretKeyName] = SecretCanary,
        });

        var secret = await provider.GetAsync(SecretReference, Ct);

        secret.IsError.ShouldBeFalse();
        secret.Value.ShouldBe(SecretCanary);
    }

    [Fact]
    public async Task A_bare_name_is_its_own_key()
    {
        var provider = Provider(new Dictionary<string, string?>
        {
            [SecretKeyName] = SecretCanary,
        });

        var secret = await provider.GetAsync(SecretKeyName, Ct);

        secret.Value.ShouldBe(SecretCanary);
    }

    [Fact]
    public async Task The_whole_reference_is_never_read_as_a_key()
    {
        // A source that carries the full path as a key must not be able to shadow the secret the name resolves to.
        var provider = Provider(new Dictionary<string, string?>
        {
            [SecretReference] = SecretCanary,
        });

        var secret = await provider.GetAsync(SecretReference, Ct);

        secret.IsError.ShouldBeTrue();
        secret.FirstError.Code.ShouldBe(SecretUnavailable);
    }

    [Fact]
    public async Task A_missing_secret_fails_without_the_reference_or_a_value()
    {
        var secret = await Provider().GetAsync(SecretReference, Ct);

        secret.IsError.ShouldBeTrue();
        secret.FirstError.Code.ShouldBe(SecretUnavailable);
        secret.FirstError.Description.ShouldNotContain(SecretReference);
        secret.FirstError.Description.ShouldNotContain(SecretKeyName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_value_is_a_missing_secret(string value)
    {
        var provider = Provider(new Dictionary<string, string?>
        {
            [SecretKeyName] = value,
        });

        var secret = await provider.GetAsync(SecretReference, Ct);

        secret.IsError.ShouldBeTrue();
        secret.FirstError.Code.ShouldBe(SecretUnavailable);
        secret.FirstError.Description.ShouldNotContain(SecretCanary);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nts/dev/api/")]
    [InlineData("nts/dev/api")]
    public async Task A_reference_without_a_name_fails_closed(string reference)
    {
        // `nts/dev/api` must read the key `api`, which no source carries, rather than falling back to a neighbour.
        var secret = await Provider().GetAsync(reference, Ct);

        secret.IsError.ShouldBeTrue();
        secret.FirstError.Code.ShouldBe(SecretUnavailable);
    }

    private static ConfigurationSecretProvider Provider(Dictionary<string, string?>? values = null) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(values ?? []).Build());
}
