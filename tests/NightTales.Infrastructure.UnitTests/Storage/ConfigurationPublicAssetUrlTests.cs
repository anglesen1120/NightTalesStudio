using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Infrastructure.Storage;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Storage;

/// <summary>
/// The URL composition of 09.4.2-T1 (plan §3): the join, and every shape that must answer
/// <see langword="null"/> instead of a URL the platform does not control.
/// </summary>
public sealed class ConfigurationPublicAssetUrlTests
{
    private const string BaseUrl = "https://nt-public-dev.example.test";
    private const string BucketBaseUrl = "https://storage.example.test/nt-public-prod/";
    private const string Key = "voice-previews/thien-minh.v1.mp3";

    // Microsoft.Extensions.Hosting.Environments members are static readonly, not const, so a host name
    // must be spelled as a literal where the C# language requires a compile-time constant (attribute
    // arguments, default parameter values).
    private const string Production = "Production";
    private const string Development = "Development";

    [Theory]
    [InlineData("https://nt-public-dev.example.test")]
    [InlineData("https://nt-public-dev.example.test/")]
    public void Build_Joins_Base_And_Key(string baseUrl)
    {
        // The trailing slash of the base decides whether the key is appended or replaces the base's
        // last segment, so both spellings must produce the same URL.
        Builder(baseUrl).Build(Key).ShouldNotBeNull().AbsoluteUri.ShouldBe($"{BaseUrl}/{Key}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_Null_Key_Returns_Null(string? key) => Builder(BaseUrl).Build(key).ShouldBeNull();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_Empty_Base_Returns_Null(string baseUrl) => Builder(baseUrl).Build(Key).ShouldBeNull();

    [Theory]
    [InlineData("https://evil.example.test/x.mp3")]
    [InlineData("http://evil.example.test/x.mp3")]
    // Not well-formed (the space is unescaped) but Uri.TryCreate still parses it as absolute, so it
    // must be refused like the well-formed forms above.
    [InlineData("https://evil.example.test/a b.mp3")]
    [InlineData("c:/x.mp3")]
    public void Build_Absolute_Key_Returns_Null(string key) => Builder(BaseUrl).Build(key).ShouldBeNull();

    [Theory]
    [InlineData("../secrets/x.mp3")]
    [InlineData("voice-previews/../../x.mp3")]
    [InlineData("..")]
    [InlineData("voice-previews/..")]
    public void Build_DotDot_Key_Returns_Null(string key) => Builder(BaseUrl).Build(key).ShouldBeNull();

    /// <summary>
    /// A base that carries a bucket path, and keys whose percent-encoded dots <see cref="Uri"/>
    /// decodes and collapses while composing, so the raw-string check alone would let them escape the
    /// bucket prefix.
    /// </summary>
    [Theory]
    [InlineData("%2e%2e/outside.mp3")]
    [InlineData(".%2e/outside.mp3")]
    [InlineData("voice-previews/%2e%2e/outside.mp3")]
    [InlineData("voice/%2e%2e")]
    public void Build_PercentEncoded_Traversal_Returns_Null(string key) =>
        Builder(BucketBaseUrl).Build(key).ShouldBeNull();

    [Fact]
    public void Build_With_Bucket_Base_Keeps_Key_Under_Bucket_Path() =>
        Builder(BucketBaseUrl).Build(Key).ShouldNotBeNull().AbsoluteUri
            .ShouldBe($"https://storage.example.test/nt-public-prod/{Key}");

    [Theory]
    [InlineData("/voice-previews/x.mp3")]
    [InlineData("\\voice-previews\\x.mp3")]
    [InlineData("voice-previews\\x.mp3")]
    public void Build_RootRelative_Or_Backslash_Key_Returns_Null(string key) =>
        Builder(BaseUrl).Build(key).ShouldBeNull();

    [Theory]
    [InlineData("nt-public-dev.example.test")]
    [InlineData("//nt-public-dev.example.test")]
    [InlineData("not a url")]
    public void Build_Unparseable_Base_Returns_Null(string baseUrl) =>
        Builder(baseUrl).Build(Key).ShouldBeNull();

    /// <summary>
    /// The scheme rule of plan §3: outside Development an <c>http</c> base is refused unless the host
    /// explicitly turned <c>RequireHttps</c> off, while Development ignores the flag for its local
    /// emulator.
    /// </summary>
    [Theory]
    [InlineData(Production, true, null)]
    [InlineData(Production, false, "http://localhost:9000/voice-previews/x.mp3")]
    [InlineData(Development, true, "http://localhost:9000/voice-previews/x.mp3")]
    public void Build_Http_Rejected_When_RequireHttps(
        string environmentName,
        bool requireHttps,
        string? expected)
    {
        var url = Builder("http://localhost:9000", requireHttps, environmentName)
            .Build("voice-previews/x.mp3");

        if (expected is null)
        {
            url.ShouldBeNull();
        }
        else
        {
            url.ShouldNotBeNull().AbsoluteUri.ShouldBe(expected);
        }
    }

    private static ConfigurationPublicAssetUrl Builder(
        string baseUrl,
        bool requireHttps = true,
        string environmentName = Production) =>
        new(
            Options.Create(new StorageOptions { PublicBaseUrl = baseUrl, RequireHttps = requireHttps }),
            new StubHostEnvironment { EnvironmentName = environmentName },
            NullLogger<ConfigurationPublicAssetUrl>.Instance);

    /// <summary>A minimal <see cref="IHostEnvironment"/>: only the name is read by the builder.</summary>
    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Production;

        public string ApplicationName { get; set; } = "NightTales.Infrastructure.UnitTests";

        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
