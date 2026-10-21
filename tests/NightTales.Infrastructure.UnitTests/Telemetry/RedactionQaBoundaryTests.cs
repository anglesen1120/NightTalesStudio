using NightTales.Infrastructure.Telemetry.Redaction;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Telemetry;

/// <summary>09.9.3-T2 QA additions (written by the QA agent): boundary and negative cases of the pure redactor.</summary>
public sealed class RedactionQaBoundaryTests
{
    private static readonly IReadOnlySet<string> Keys = RedactionOptions.Default.QueryKeys;

    [Theory]
    [InlineData("?token=a=b", "?token=<redacted>")]
    [InlineData("?token=a&token=b", "?token=<redacted>&token=<redacted>")]
    [InlineData("?&&token=a&&", "?&&token=<redacted>&&")]
    [InlineData("?token=%41%42", "?token=<redacted>")]
    [InlineData("?state=a%20b+c", "?state=<redacted>")]
    [InlineData("?x=1&code=", "?x=1&code=")]
    [InlineData("?token", "?token")]
    [InlineData("?=token", "?=token")]
    [InlineData("?tokenx=a&xtoken=b&mytoken=c", "?tokenx=a&xtoken=b&mytoken=c")]
    public void Boundary_shapes(string query, string expected) =>
        QueryStringRedactor.Redact(query, Keys).ShouldBe(expected);

    [Theory]
    [InlineData("?to%6Ben=raw", "?to%6Ben=<redacted>")]
    [InlineData("?%74icket=raw", "?%74icket=<redacted>")]
    [InlineData("?TiCkEt=raw", "?TiCkEt=<redacted>")]
    public void Encoded_and_mixed_case_keys_are_matched(string query, string expected) =>
        QueryStringRedactor.Redact(query, Keys).ShouldBe(expected);

    [Theory]
    [InlineData("https://h.test/cb?code=raw&state=raw2", "https://h.test/cb?code=<redacted>&state=<redacted>")]
    [InlineData("https://h.test/p#/reset?token=raw", "https://h.test/p#<redacted>")]
    [InlineData("/hubs/episodes?ticket=raw#frag", "/hubs/episodes?ticket=<redacted>#<redacted>")]
    [InlineData("/plain/path", "/plain/path")]
    public void Url_shapes(string url, string expected) =>
        QueryStringRedactor.RedactUrl(url, Keys).ShouldBe(expected);

    [Theory]
    [InlineData("failed for /reset?token=raw", "failed for /reset?token=<redacted>")]
    [InlineData("token=raw", "token=<redacted>")]
    [InlineData("a code=raw, b state=raw2; done", "a code=<redacted>, b state=<redacted>; done")]
    [InlineData("\"ticket=raw\" quoted", "\"ticket=<redacted>\" quoted")]
    [InlineData("no pairs here", "no pairs here")]
    [InlineData("unrelated=value", "unrelated=value")]
    public void Free_text_shapes(string text, string expected) =>
        QueryStringRedactor.RedactText(text, Keys).ShouldBe(expected);

    [Fact]
    public void Very_long_query_is_masked_without_loss_of_other_parameters()
    {
        var filler = string.Join('&', Enumerable.Range(0, 5000).Select(i => $"k{i}=v{i}"));

        var result = QueryStringRedactor.Redact($"?{filler}&ticket=raw-secret-value", Keys);

        result.ShouldEndWith("&ticket=<redacted>");
        result.ShouldNotContain("raw-secret-value");
        result.ShouldStartWith("?k0=v0&k1=v1");
    }

    [Fact]
    public void Configuration_can_add_but_never_remove_a_frozen_key()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationManager();
        configuration["Logging:RedactionQueryKeys"] = "sig,Apikey";

        var options = RedactionOptions.Resolve(configuration);

        options.QueryKeys.ShouldBe(
            ["access_token", "ticket", "token", "code", "state", "upload_id", "sig", "Apikey"],
            ignoreOrder: true);
        QueryStringRedactor.Redact("?APIKEY=raw&token=raw", options.QueryKeys).ShouldBe("?APIKEY=<redacted>&token=<redacted>");
    }
}
