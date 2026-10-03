// The Responses API of OpenAI 2.14.0 is marked experimental (OPENAI001); the adapter under test uses it.
#pragma warning disable OPENAI001

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NightTales.Application.Abstractions.Text;
using NightTales.Domain.Stories.Content;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.OpenAI;

/// <summary>The output type the probe schema describes: both properties are required.</summary>
internal sealed record Probe(string Title, int Count);

/// <summary>
/// 02.2.1-T2: one test per row of the D7 outcome table, over the real registration (named HttpClient, per-step
/// Polly pipeline, SDK retries off) and a scripted transport — no live call.
/// </summary>
public sealed class OpenAiTextClientTests
{
    private const string OkJson = """{"title":"T","count":3}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] MaxTokensRule = ["max_output_tokens_exceeded"];
    private static readonly string[] SchemaInvalidRule = ["schema_invalid"];

    [Fact]
    public async Task OpenAiTextClient_ValidJson_ReturnsOk()
    {
        using var host = new OpenAiTestHost();
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(OkJson), ("x-request-id", "req_abc"));

        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        var ok = result.ShouldBeOfType<TextCallOk<Probe>>();
        ok.Draft.ShouldBe(new Probe("T", 3));
        ok.Usage.ShouldBe(new TokenUsage(100, 20, 0, 50, 10));
        ok.RequestId.ShouldBe("req_abc");
        ok.Model.ShouldBe(OpenAiTestHost.Model);
        host.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task OpenAiTextClient_ValidStoryJson_DeserialisesSnakeCaseIntoStoryContentV1()
    {
        const string Story = """
            {"title":"Ngôi nhà","hook":{"text":"h","keywords":["door"],"fact_refs":[]},
             "paragraphs":[{"text":"p","keywords":["hall"],"fact_refs":["f1"],"beat":"setup"}],
             "ending":{"text":"e","keywords":["night"],"fact_refs":[]},
             "description":"d","tags":["ghost"],"content_warnings":[],
             "structure":{"sub_genre":"haunted_house","setting":"house","opening":"cold","twist_type":"reveal","narrator_mode":"first"}}
            """;
        using var host = new OpenAiTestHost();
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(Story));

        var result = await host.Client.CallAsync<StoryContentV1>(OpenAiTestHost.Request(), Ct);

        var draft = result.ShouldBeOfType<TextCallOk<StoryContentV1>>().Draft;
        draft.Title.ShouldBe("Ngôi nhà");
        draft.Paragraphs.Single().Beat.ShouldBe("setup");
        draft.Structure.TwistType.ShouldBe("reveal");
    }

    [Fact]
    public async Task OpenAiTextClient_SchemaInvalidJson_ReturnsInvalidOutputWithSchemaInvalid()
    {
        using var host = new OpenAiTestHost();
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed("{not json"));

        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        var invalid = result.ShouldBeOfType<TextCallInvalidOutput<Probe>>();
        invalid.RuleIds.ShouldBe(SchemaInvalidRule);
        invalid.Usage.ShouldNotBeNull();
    }

    [Fact]
    public async Task OpenAiTextClient_MissingRequiredProperty_ReturnsInvalidOutput()
    {
        using var host = new OpenAiTestHost();
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed("""{"title":"T"}"""));

        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        result.ShouldBeOfType<TextCallInvalidOutput<Probe>>().RuleIds.ShouldBe(SchemaInvalidRule);
    }

    [Fact]
    public async Task OpenAiTextClient_MaxOutputTokensReason_ReturnsInvalidOutputWithMaxTokensRuleId()
    {
        using var host = new OpenAiTestHost();
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed("{\"title\":", status: "incomplete", incomplete: "max_output_tokens"));

        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        result.ShouldBeOfType<TextCallInvalidOutput<Probe>>().RuleIds.ShouldBe(MaxTokensRule);
    }

    [Fact]
    public async Task OpenAiTextClient_RefusalPart_ReturnsModerated()
    {
        using var host = new OpenAiTestHost();
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Refusal());

        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        var moderated = result.ShouldBeOfType<TextCallModerated<Probe>>();
        moderated.Categories.ShouldBeEmpty();
        moderated.Usage!.InputTokens.ShouldBe(100);
    }

    [Fact]
    public async Task OpenAiTextClient_ContentFilterReason_ReturnsModeratedWithCategories()
    {
        using var host = new OpenAiTestHost();
        var body = OpenAiTestHost.Completed("", status: "incomplete", incomplete: "content_filter")
            .Replace("\"output\":", "\"content_filter_results\":{\"violence\":{\"filtered\":true},\"hate\":{\"filtered\":false}},\"output\":", StringComparison.Ordinal);
        host.Handler.Reply(HttpStatusCode.OK, body);

        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        result.ShouldBeOfType<TextCallModerated<Probe>>().Categories.ShouldBe(["violence"]);
    }

    [Fact]
    public async Task OpenAiTextClient_SpendLimitExceeded_ReturnsProviderFundsExhausted()
    {
        using var host = new OpenAiTestHost();
        host.Handler.Reply(HttpStatusCode.TooManyRequests, OpenAiTestHost.Error("monthly_spend_limit_exceeded"), ("x-request-id", "req_funds"));

        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        result.ShouldBeOfType<TextCallFundsExhausted<Probe>>().RequestId.ShouldBe("req_funds");
        host.Handler.Requests.Count.ShouldBe(1, "a funds failure is never retried");
    }

    [Fact]
    public async Task OpenAiTextClient_CreditBalanceExhausted_ReturnsProviderFundsExhausted()
    {
        using var host = new OpenAiTestHost();
        host.Handler.Reply(HttpStatusCode.TooManyRequests, OpenAiTestHost.Error("credit_balance_exhausted"));

        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        result.ShouldBeOfType<TextCallFundsExhausted<Probe>>();
        host.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task OpenAiTextClient_FundsExhaustedFlag_ShortCircuitsBeforeAnyRequest()
    {
        using var host = new OpenAiTestHost();
        host.Flags.FundsExhausted = true;

        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        var funds = result.ShouldBeOfType<TextCallFundsExhausted<Probe>>();
        funds.RequestId.ShouldBeNull();
        host.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenAiTextClient_Other4xx_ReturnsProviderErrorWithStatus()
    {
        using var host = new OpenAiTestHost();
        host.Handler.Reply(HttpStatusCode.BadRequest, OpenAiTestHost.Error("invalid_request"));

        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        result.ShouldBeOfType<TextCallProviderError<Probe>>().Status.ShouldBe(400);
        host.Handler.Requests.Count.ShouldBe(1, "a 4xx is not retried");
    }

    [Fact]
    public async Task OpenAiTextClient_TimeoutAfterRetry_ReturnsProviderUnavailable()
    {
        using var host = new OpenAiTestHost(o => o.RequestTimeout[TextStep.Writer] = TimeSpan.FromMilliseconds(150));
        for (var i = 0; i < 2; i++)
        {
            host.Handler.Then(async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);

                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        }

        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        result.ShouldBeOfType<TextCallUnavailable<Probe>>();
        host.Handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task OpenAiTextClient_ServerErrorAfterRetry_ReturnsProviderUnavailable()
    {
        using var host = new OpenAiTestHost();
        host.Handler
            .Reply(HttpStatusCode.InternalServerError, OpenAiTestHost.Error("server_error"))
            .Reply(HttpStatusCode.BadGateway, OpenAiTestHost.Error("server_error"), ("x-request-id", "req_5xx"));

        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        result.ShouldBeOfType<TextCallUnavailable<Probe>>().RequestId.ShouldBe("req_5xx");
        host.Handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task OpenAiTextClient_429WithoutFundsCode_IsRetriedThenUnavailable()
    {
        using var host = new OpenAiTestHost();
        host.Handler
            .Reply(HttpStatusCode.TooManyRequests, OpenAiTestHost.Error("rate_limit_exceeded"))
            .Reply(HttpStatusCode.TooManyRequests, OpenAiTestHost.Error("rate_limit_exceeded"));

        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        result.ShouldBeOfType<TextCallUnavailable<Probe>>();
        host.Handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task OpenAiTextClient_429ThenOk_RetriesOnceAndSucceeds()
    {
        using var host = new OpenAiTestHost();
        host.Handler
            .Reply(HttpStatusCode.TooManyRequests, OpenAiTestHost.Error("rate_limit_exceeded"))
            .Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(OkJson));

        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        result.ShouldBeOfType<TextCallOk<Probe>>();

        // Exactly 2 attempts proves the SDK's own retry is off and the pipeline is the only retry layer (F23):
        // a lingering SDK retry would multiply them to 4.
        host.Handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task OpenAiTextClient_RetryAfterHeaderIsHonoured()
    {
        using var host = new OpenAiTestHost();
        host.Handler
            .Reply(HttpStatusCode.TooManyRequests, OpenAiTestHost.Error("rate_limit_exceeded"), ("Retry-After", "1"))
            .Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(OkJson));

        var watch = Stopwatch.StartNew();
        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);
        watch.Stop();

        result.ShouldBeOfType<TextCallOk<Probe>>();
        watch.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900), "the 1 ms backoff alone would not wait that long");
    }

    [Fact]
    public async Task OpenAiTextClient_SetsStoreFalseSafetyIdentifierAndRequestIdHeader()
    {
        var userId = Guid.Parse("0197a000-0000-7000-8000-000000000001");
        using var host = new OpenAiTestHost();
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(OkJson));
        var request = OpenAiTestHost.Request(userId: userId);

        await host.Client.CallAsync<Probe>(request, Ct);

        var sent = host.Handler.Requests.Single();
        var body = sent.Json;
        body.GetProperty("store").GetBoolean().ShouldBeFalse();
        body.GetProperty("safety_identifier").GetString().ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(userId.ToString()))));
        body.GetProperty("prompt_cache_key").GetString().ShouldBe("writer:ghost.vi.v1");
        sent.Headers["X-Client-Request-Id"].ShouldBe($"{request.Run.GenerationRunId}:writer:1");
        sent.Headers["Authorization"].ShouldStartWith("Bearer ");
        sent.Uri!.AbsoluteUri.ShouldBe("https://api.aixingialaire.shop/v1/responses");
    }

    [Fact]
    public async Task OpenAiTextClient_SystemPromptFirstUserDataLast()
    {
        using var host = new OpenAiTestHost();
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(OkJson));

        await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        var input = host.Handler.Requests.Single().Json.GetProperty("input").EnumerateArray().ToArray();
        input.Length.ShouldBe(2);
        input[0].GetProperty("role").GetString().ShouldBe("system");
        input[0].ToString().ShouldContain("SYSTEM-PROMPT-SECRET");
        input[1].GetProperty("role").GetString().ShouldBe("user");
        input[1].ToString().ShouldContain("DATA-OBJECT-SECRET");
    }

    [Fact]
    public async Task OpenAiTextClient_StrictJsonSchemaFormatWithSanitisedName()
    {
        using var host = new OpenAiTestHost();
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(OkJson));

        await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        var format = host.Handler.Requests.Single().Json.GetProperty("text").GetProperty("format");
        format.GetProperty("type").GetString().ShouldBe("json_schema");
        format.GetProperty("strict").GetBoolean().ShouldBeTrue();
        format.GetProperty("name").GetString().ShouldBe("probe_v1");
        format.GetProperty("schema").GetProperty("required").GetArrayLength().ShouldBe(2);
    }

    [Theory]
    [InlineData(TextStep.Writer, "medium")]
    [InlineData(TextStep.Review, "low")]
    public async Task OpenAiTextClient_ReasoningEffortFromConfig(TextStep step, string effort)
    {
        using var host = new OpenAiTestHost();
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(OkJson));

        await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(step), Ct);

        var body = host.Handler.Requests.Single().Json;
        body.GetProperty("reasoning").GetProperty("effort").GetString().ShouldBe(effort);
        body.GetProperty("model").GetString().ShouldBe(OpenAiTestHost.Model);
    }

    [Fact]
    public async Task OpenAiTextClient_EffortAndModelOverrideAreRead()
    {
        using var host = new OpenAiTestHost(o =>
        {
            o.Effort[TextStep.Writer] = "high";
            o.Models[TextStep.Writer] = "gpt-6-sol";
        });
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(OkJson));

        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        var body = host.Handler.Requests.Single().Json;
        body.GetProperty("reasoning").GetProperty("effort").GetString().ShouldBe("high");
        body.GetProperty("model").GetString().ShouldBe("gpt-6-sol");
        result.Model.ShouldBe("gpt-6-sol");
    }

    [Fact]
    public async Task OpenAiTextClient_MaxOutputTokensFromRequest()
    {
        using var host = new OpenAiTestHost();
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(OkJson));

        await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(maxOutput: 4321), Ct);

        host.Handler.Requests.Single().Json.GetProperty("max_output_tokens").GetInt32().ShouldBe(4321);
    }

    [Fact]
    public async Task OpenAiTextClient_UsagePresentOnFailure_IsReturned()
    {
        using var host = new OpenAiTestHost();
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed("{\"title\":", status: "incomplete", incomplete: "max_output_tokens"));

        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        result.ShouldBeOfType<TextCallInvalidOutput<Probe>>();
        result.Usage.ShouldNotBeNull();
        result.Usage.InputTokens.ShouldBe(100);
        result.Usage.OutputTokens.ShouldBe(50);
    }

    [Fact]
    public async Task OpenAiTextClient_UsageAbsent_ReturnsNullUsage()
    {
        using var host = new OpenAiTestHost();
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(OkJson, usage: string.Empty));

        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        result.ShouldBeOfType<TextCallOk<Probe>>().Usage.ShouldBeNull();
    }

    [Fact]
    public async Task OpenAiTextClient_RequestIdHeaderIsCaptured()
    {
        using var host = new OpenAiTestHost();
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(OkJson), ("x-request-id", "req_captured"));
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(OkJson));

        (await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct)).RequestId.ShouldBe("req_captured");
        (await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct)).RequestId.ShouldBeNull();
    }

    [Fact]
    public async Task OpenAiTextClient_NoContentInLogs()
    {
        using var host = new OpenAiTestHost();
        host.Handler
            .Reply(HttpStatusCode.OK, OpenAiTestHost.Completed("""{"title":"DRAFT-TEXT-SECRET","count":1}"""))
            .Reply(HttpStatusCode.OK, OpenAiTestHost.Refusal())
            .Reply(HttpStatusCode.BadRequest, """{"error":{"message":"echo SYSTEM-PROMPT-SECRET DATA-OBJECT-SECRET","code":"x"}}""");

        await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);
        await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);
        await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        host.Logs.Messages.ShouldNotBeEmpty("each call logs one structured line");
        var all = string.Join("\n", host.Logs.Messages);
        all.ShouldNotContain("SYSTEM-PROMPT-SECRET");
        all.ShouldNotContain("DATA-OBJECT-SECRET");
        all.ShouldNotContain("DRAFT-TEXT-SECRET");
        all.ShouldNotContain("REFUSAL-TEXT");
        all.ShouldNotContain("unit-test-api-key-placeholder");
        all.ShouldContain("OpenAI call Writer attempt 1");
    }

    [Fact]
    public async Task OpenAiTextClient_UnexpectedExceptionIsMappedNotThrown()
    {
        using var host = new OpenAiTestHost();
        host.Handler.Then(_ => throw new FormatException("boom"));

        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        result.ShouldBeOfType<TextCallProviderError<Probe>>().Status.ShouldBe(0);
        string.Join("\n", host.Logs.Messages).ShouldNotContain("SYSTEM-PROMPT-SECRET");
    }

    [Fact]
    public async Task OpenAiTextClient_CallerCancellation_Propagates()
    {
        using var host = new OpenAiTestHost();
        host.Handler.Then(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(100);

        await Should.ThrowAsync<OperationCanceledException>(async () => await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), cts.Token));
    }

    [Fact]
    public async Task OpenAiTextClient_SecondAttemptRunSendsAttemptTwoInTheHeader()
    {
        using var host = new OpenAiTestHost();
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(OkJson));
        var request = OpenAiTestHost.Request(TextStep.Revision, attempt: 2);

        await host.Client.CallAsync<Probe>(request, Ct);

        host.Handler.Requests.Single().Headers["X-Client-Request-Id"].ShouldBe($"{request.Run.GenerationRunId}:revision:2");
    }
}
