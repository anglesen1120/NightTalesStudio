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

    [Theory]
    [InlineData("requestUrl=/reset?token=raw", "requestUrl=/reset?token=<redacted>")]
    [InlineData("next=/cb?code=x", "next=/cb?code=<redacted>")]
    [InlineData("a=%2Fp%3Fticket%3Dx", "a=<redacted>")]
    [InlineData("a=%252Fp%253Fticket%253Dx", "a=<redacted>")]
    [InlineData("failed for next=/cb?code=x&y=1", "failed for next=/cb?code=<redacted>&y=1")]
    public void Free_text_finds_listed_pairs_nested_in_unlisted_values(string text, string expected)
    {
        var redacted = QueryStringRedactor.RedactText(text, Keys);

        redacted.ShouldBe(expected);
        QueryStringRedactor.RedactText(redacted, Keys).ShouldBe(expected);
    }

    [Theory]
    [InlineData("token=\"abc\"", "token=\"<redacted>\"")]
    [InlineData("ticket='abc'", "ticket='<redacted>'")]
    [InlineData("token=\"two words\" tail", "token=\"<redacted>\" tail")]
    [InlineData("{\"x\":1} state='a;b,c' end", "{\"x\":1} state='<redacted>' end")]
    public void Free_text_masks_quoted_values(string text, string expected)
    {
        var redacted = QueryStringRedactor.RedactText(text, Keys);

        redacted.ShouldBe(expected);
        QueryStringRedactor.RedactText(redacted, Keys).ShouldBe(expected);
    }

    [Theory]
    [InlineData("/probe/redaction/throw?token=prefix;secret", "/probe/redaction/throw?token=<redacted>")]
    [InlineData("GET /p?a=1&code=x,y(z) failed", "GET /p?a=1&code=<redacted> failed")]
    [InlineData("/p?ticket=a;b&x=1#frag", "/p?ticket=<redacted>&x=1#frag")]
    public void Url_values_run_to_the_query_boundary_not_prose_delimiters(string text, string expected) =>
        QueryStringRedactor.RedactText(text, Keys).ShouldBe(expected);

    [Fact]
    public void Prose_pairs_still_stop_at_prose_delimiters() =>
        QueryStringRedactor.RedactText("failed (token=abc), retrying; state=x end", Keys)
            .ShouldBe("failed (token=<redacted>), retrying; state=<redacted> end");

    [Theory]
    [InlineData("?next=/cb?code=x", "?next=/cb?code=<redacted>")]
    [InlineData("?a=1&next=/cb?ticket=x&b=2", "?a=1&next=/cb?ticket=<redacted>&b=2")]
    [InlineData("?a=%2Fp%3Fticket%3Dx", "?a=<redacted>")]
    [InlineData("?z=1&a=%2Fp%3Fticket%3Dx&b=2", "?z=1&a=<redacted>&b=2")]
    public void Query_masks_listed_pairs_nested_in_unlisted_segments(string query, string expected)
    {
        var redacted = QueryStringRedactor.Redact(query, Keys);

        redacted.ShouldBe(expected);
        QueryStringRedactor.Redact(redacted, Keys).ShouldBeSameAs(redacted);
    }

    [Fact]
    public void Unlisted_nested_pairs_leave_the_same_instance()
    {
        var query = "?next=/p?x=1&a=%2Fp%3Fx%3D1";
        var text = "next=/p?x=1 a=%2Fp%3Fx%3D1";

        QueryStringRedactor.Redact(query, Keys).ShouldBeSameAs(query);
        QueryStringRedactor.RedactText(text, Keys).ShouldBeSameAs(text);
    }

    [Fact]
    public void Url_masks_nested_pairs_in_the_query()
    {
        QueryStringRedactor.RedactUrl("https://x.test/login?next=/cb?code=x&a=1", Keys)
            .ShouldBe("https://x.test/login?next=/cb?code=<redacted>&a=1");
    }
}
