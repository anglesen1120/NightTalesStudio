using NightTales.Infrastructure.Telemetry.Redaction;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Telemetry;

/// <summary>09.9.3-T2: the pure query-string redactor — no host, no logger.</summary>
public sealed class QueryStringRedactorTests
{
    private static readonly IReadOnlySet<string> Keys = RedactionOptions.Default.QueryKeys;

    [Theory]
    [InlineData("access_token=raw1", "access_token=<redacted>")]
    [InlineData("?ticket=raw1", "?ticket=<redacted>")]
    [InlineData("TOKEN=raw1", "TOKEN=<redacted>")]
    [InlineData("?Code=raw1", "?Code=<redacted>")]
    [InlineData("state=raw1", "state=<redacted>")]
    [InlineData("to%6Ben=raw1", "to%6Ben=<redacted>")]
    public void Redacts_frozen_keys_case_insensitively(string query, string expected) =>
        QueryStringRedactor.Redact(query, Keys).ShouldBe(expected);

    [Fact]
    public void Keeps_key_order_and_other_parameters() =>
        QueryStringRedactor.Redact("?a=1&token=raw1&episodeId=7&ticket=raw2&b", Keys)
            .ShouldBe("?a=1&token=<redacted>&episodeId=7&ticket=<redacted>&b");

    [Fact]
    public void Returns_same_instance_when_no_key_matches()
    {
        var query = "?episodeId=42&page=2";

        QueryStringRedactor.Redact(query, Keys).ShouldBeSameAs(query);
    }

    [Fact]
    public void Null_and_empty_pass_through()
    {
        QueryStringRedactor.Redact(null, Keys).ShouldBeNull();
        QueryStringRedactor.Redact(string.Empty, Keys).ShouldBeSameAs(string.Empty);
        QueryStringRedactor.Redact("?", Keys).ShouldBe("?");
    }

    [Theory]
    [InlineData("?ticket=abc%zz&token=x")]
    [InlineData("?episodeId=1%")]
    [InlineData("?a=%4")]
    public void Always_masks_on_parse_failure(string query)
    {
        var redacted = QueryStringRedactor.Redact(query, Keys);

        redacted.ShouldBe("?<redacted>");
        redacted.ShouldNotContain("abc");
    }

    [Fact]
    public void Is_idempotent()
    {
        var once = QueryStringRedactor.Redact("?token=raw1", Keys);

        QueryStringRedactor.Redact(once, Keys).ShouldBeSameAs(once);
        QueryStringRedactor.Redact("?<redacted>", Keys).ShouldBe("?<redacted>");
    }

    [Fact]
    public void Configured_keys_extend_the_frozen_list()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationManager();
        configuration[$"{RedactionOptions.QueryKeysKey}:0"] = "session";
        var options = RedactionOptions.Resolve(configuration);

        QueryStringRedactor.Redact("?session=raw1&ticket=raw2", options.QueryKeys)
            .ShouldBe("?session=<redacted>&ticket=<redacted>");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" , ")]
    public void An_empty_configured_list_is_rejected(string value) =>
        Should.Throw<InvalidOperationException>(() =>
        {
            var configuration = new Microsoft.Extensions.Configuration.ConfigurationManager();
            configuration[RedactionOptions.QueryKeysKey] = value;
            RedactionOptions.Resolve(configuration);
        });

    [Fact]
    public void Url_query_and_fragment_are_masked()
    {
        QueryStringRedactor.RedactUrl("https://x.test/hubs/episodes?ticket=raw1&a=1#frag", Keys)
            .ShouldBe("https://x.test/hubs/episodes?ticket=<redacted>&a=1#<redacted>");
        QueryStringRedactor.RedactUrl("https://x.test/reset#raw1", Keys).ShouldBe("https://x.test/reset#<redacted>");
        QueryStringRedactor.RedactUrl("https://x.test/ok?a=1", Keys).ShouldBe("https://x.test/ok?a=1");
        QueryStringRedactor.RedactUrl("/p?ticket=abc%zz", Keys).ShouldBe("/p?<redacted>");
    }

    [Fact]
    public void Free_text_pairs_are_masked_and_other_text_kept()
    {
        QueryStringRedactor.RedactText("GET /reset?token=raw1&x=2 failed (state=raw2)", Keys)
            .ShouldBe("GET /reset?token=<redacted>&x=2 failed (state=<redacted>)");

        var plain = "nothing to see: a=b";
        QueryStringRedactor.RedactText(plain, Keys).ShouldBeSameAs(plain);
        QueryStringRedactor.RedactText("my_token=raw1", Keys).ShouldBe("my_token=raw1");
    }
}
