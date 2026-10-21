using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using NightTales.Application.Abstractions.Text;
using NightTales.Infrastructure.OpenAI;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.OpenAI;

/// <summary>02.2.1-T2 step 1 / failure rows: a missing key and a non-pinned base URL fail the start-up, not the first call.</summary>
public sealed class OpenAiOptionsValidatorTests
{
    private static OpenAiOptions Valid() => new() { ApiKey = "unit-test-api-key-placeholder" };

    [Fact]
    public void Validate_PinnedBaseUrlAndKey_Succeeds() =>
        new OpenAiOptionsValidator(new Env(Environments.Production)).Validate(null, Valid()).Succeeded.ShouldBeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_MissingApiKey_FailsInEveryEnvironment(string key)
    {
        var options = Valid();
        options.ApiKey = key;

        foreach (var environment in new[] { Environments.Development, Environments.Production })
        {
            var result = new OpenAiOptionsValidator(new Env(environment)).Validate(null, options);

            result.Failed.ShouldBeTrue(environment);
            result.FailureMessage.ShouldContain("OpenAi:ApiKey");
        }
    }

    [Fact]
    public void Validate_NonPinnedBaseUrl_IsRefusedOutsideDevelopmentAndAcceptedInDevelopment()
    {
        var options = Valid();
        options.BaseUrl = "http://127.0.0.1:9100/v1";

        new OpenAiOptionsValidator(new Env(Environments.Production)).Validate(null, options).Failed.ShouldBeTrue();
        new OpenAiOptionsValidator(new Env(Environments.Development)).Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("http://127.0.0.1:9100/v1")]
    [InlineData("http://localhost:9100/v1")]
    [InlineData("http://[::1]:9100/v1")]
    [InlineData("https://relay.internal.example/v1")]
    public void Validate_Development_AcceptsLoopbackOrHttps(string url)
    {
        var options = Valid();
        options.BaseUrl = url;

        new OpenAiOptionsValidator(new Env(Environments.Development)).Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("http://relay.internal.example/v1")]
    [InlineData("http://10.0.0.5:9100/v1")]
    public void Validate_Development_RefusesPlainHttpToANonLoopbackHost(string url)
    {
        var options = Valid();
        options.BaseUrl = url;

        var result = new OpenAiOptionsValidator(new Env(Environments.Development)).Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("loopback");
    }

    [Fact]
    public void Validate_PinnedBaseUrlWithTrailingSlash_Succeeds()
    {
        var options = Valid();
        options.BaseUrl = OpenAiOptions.PinnedBaseUrl + "/";

        new OpenAiOptionsValidator(new Env(Environments.Production)).Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Validate_UnknownEffortAndMissingModel_FailWithEveryProblem()
    {
        var options = Valid();
        options.Effort[TextStep.Writer] = "turbo";
        options.Models.Remove(TextStep.Facts);
        options.RequestTimeout[TextStep.Review] = TimeSpan.Zero;

        var result = new OpenAiOptionsValidator(new Env(Environments.Production)).Validate(null, options);

        result.Failures.ShouldNotBeNull();
        result.Failures.Count().ShouldBe(3);
    }

    [Fact]
    public void Defaults_FollowD7()
    {
        var options = new OpenAiOptions();

        options.Effort[TextStep.Writer].ShouldBe("medium");
        options.Effort[TextStep.Finder].ShouldBe("low");
        options.RequestTimeout[TextStep.Writer].ShouldBe(TimeSpan.FromSeconds(90));
        options.RequestTimeout[TextStep.Review].ShouldBe(TimeSpan.FromSeconds(45));
        options.RequestTimeout[TextStep.Facts].ShouldBe(TimeSpan.FromSeconds(60));
        options.RequestTimeout[TextStep.Ideas].ShouldBe(TimeSpan.FromSeconds(30));
        options.Models.Values.ShouldAllBe(model => model == "gpt-6-luna");
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "test";

        public string ContentRootPath { get; set; } = "/";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
