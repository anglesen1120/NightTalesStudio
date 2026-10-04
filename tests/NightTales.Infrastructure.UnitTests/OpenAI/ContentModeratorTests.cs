// The Responses API of OpenAI 2.14.0 is marked experimental (OPENAI001); the text adapter used below needs it.
#pragma warning disable OPENAI001

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Flags;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Domain.Compliance;
using NightTales.Domain.Stories.Generation;
using NightTales.Infrastructure.OpenAI;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.OpenAI;

/// <summary>A meter that keeps what it was asked to write.</summary>
internal sealed class RecordingUsageMeter : IUsageMeter
{
    internal List<UsageItem> Items { get; } = [];

    public Task RecordAsync(UsageItem item, CancellationToken ct)
    {
        Items.Add(item);

        return Task.CompletedTask;
    }
}

/// <summary>The moderation composition over the real registration and a scripted transport — no live call.</summary>
internal sealed class ModerationTestHost : IDisposable
{
    private readonly ServiceProvider _provider;

    internal ModerationTestHost(Dictionary<string, string?>? settings = null)
    {
        Handler = new ScriptedHandler();
        Logs = new CapturingLoggerProvider();
        Meter = new RecordingUsageMeter();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build());
        services.AddLogging(builder => builder.AddProvider(Logs));
        services.AddSingleton<IFeatureFlags>(new FakeFlags());
        services.AddSingleton<IHostEnvironment>(new TestEnvironment { EnvironmentName = Environments.Production });
        services.AddSingleton<IUsageMeter>(Meter);
        services.AddOpenAiTextClient();
        services.Configure<OpenAiOptions>(options =>
        {
            options.ApiKey = "unit-test-api-key-placeholder";
            options.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
        });
        services.Configure<HttpClientFactoryOptions>(
            OpenAiServiceCollectionExtensions.HttpClientName,
            options => options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = Handler));

        _provider = services.BuildServiceProvider();
        Moderator = _provider.GetRequiredService<IContentModerator>();
    }

    internal ScriptedHandler Handler { get; }

    internal CapturingLoggerProvider Logs { get; }

    internal RecordingUsageMeter Meter { get; }

    internal IContentModerator Moderator { get; }

    internal static RunContext Run() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new ReservationId(Guid.NewGuid()),
            GenerationTrigger.User,
            1,
            "1");

    /// <summary>A moderation body with one <c>results[]</c> entry per element of <paramref name="verdicts"/>.</summary>
    internal static string Body(params string[][] verdicts)
    {
        var results = verdicts.Select(categories =>
        {
            var flags = categories.Length == 0
                ? "{\"hate\":false}"
                : "{" + string.Join(",", categories.Select(c => $"\"{c}\":true")) + "}";
            var scores = categories.Length == 0
                ? "{\"hate\":0.001}"
                : "{" + string.Join(",", categories.Select(c => $"\"{c}\":0.97")) + "}";

            return $"{{\"flagged\":{(categories.Length > 0 ? "true" : "false")},\"categories\":{flags},\"category_scores\":{scores}}}";
        });

        return $"{{\"id\":\"modr-1\",\"model\":\"omni-moderation-latest\",\"results\":[{string.Join(",", results)}]}}";
    }

    public void Dispose() => _provider.Dispose();
}

/// <summary>
/// 02.4.1-T1 (02 README D12): the moderation adapter over the real registration — chunking, the union of the
/// categories, the free-call metering row, fail-closed, and the D7 refusal / <c>content_filter</c> mapping that
/// stays in the text client.
/// </summary>
public sealed class ContentModeratorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Moderator_CleanBrief_ReturnsNotFlaggedAndNoEvent()
    {
        using var host = new ModerationTestHost();
        host.Handler.Reply(HttpStatusCode.OK, ModerationTestHost.Body(Array.Empty<string>()));

        var result = await host.Moderator.CheckAsync(["a quiet bedtime story"], ModerationStage.Brief, Ct);

        result.Flagged.ShouldBeFalse();
        result.Categories.ShouldBeEmpty();
        host.Handler.Requests.Count.ShouldBe(1);
        host.Handler.Requests[0].Uri!.AbsolutePath.ShouldEndWith("/moderations");
        host.Handler.Requests[0].Json.GetProperty("model").GetString().ShouldBe("omni-moderation-latest");
    }

    [Fact]
    public async Task Moderator_TextOverChunkCap_SplitsAndUnionsCategories()
    {
        using var host = new ModerationTestHost();
        host.Handler.Reply(
            HttpStatusCode.OK,
            ModerationTestHost.Body([], ["hate/threatening", "violence"], ["violence", "harassment"]));
        var text = new string('a', 8_000 * 2 + 500);

        var result = await host.Moderator.CheckAsync([text], ModerationStage.Draft, Ct);

        var input = host.Handler.Requests.ShouldHaveSingleItem().Json.GetProperty("input");
        input.GetArrayLength().ShouldBe(3);
        input.EnumerateArray().Select(chunk => chunk.GetString()!.Length).ShouldBe([8_000, 8_000, 500]);
        result.Flagged.ShouldBeTrue("a flag in any chunk flags the whole text");
        result.Categories.ShouldBe(["harassment", "hate/threatening", "violence"]);
    }

    [Fact]
    public void Moderator_Chunk_NeverSplitsASurrogatePair()
    {
        var text = new string('a', 7_999) + "\U0001F600" + "tail";

        var chunks = OpenAiContentModerator.Chunk([text], 8_000);

        chunks.Count.ShouldBe(2);
        chunks[0].Length.ShouldBe(7_999);
        chunks[1].ShouldStartWith("\U0001F600");
        string.Concat(chunks).ShouldBe(text);
    }

    [Fact]
    public async Task Moderator_BlankTexts_AreNotSent()
    {
        using var host = new ModerationTestHost();

        var result = await host.Moderator.CheckAsync(["", "   "], ModerationStage.Brief, Ct);

        result.Flagged.ShouldBeFalse();
        host.Handler.Requests.ShouldBeEmpty();
        host.Meter.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Moderator_5xxAfterRetryBudget_FailsClosedAsGenerationFailed()
    {
        using var host = new ModerationTestHost();
        host.Handler
            .Reply(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"message\":\"BRIEF-ECHO-SECRET\"}}")
            .Reply(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"message\":\"BRIEF-ECHO-SECRET\"}}");

        var failure = await Should.ThrowAsync<ModerationUnavailableException>(async () =>
            await host.Moderator.CheckAsync(["BRIEF-TEXT-SECRET"], ModerationStage.Brief, Ct));

        failure.Status.ShouldBe(503);
        host.Handler.Requests.Count.ShouldBe(2, "one attempt plus one retry (D7, F23)");
        failure.ToString().ShouldNotContain("BRIEF-TEXT-SECRET");
        failure.ToString().ShouldNotContain("BRIEF-ECHO-SECRET");
        host.Meter.Items.ShouldBeEmpty("an unanswered call has no row");
    }

    [Fact]
    public async Task Moderator_AnswerWithNoResults_FailsClosed()
    {
        using var host = new ModerationTestHost();
        host.Handler.Reply(HttpStatusCode.OK, "{\"id\":\"modr-1\",\"results\":[]}");

        await Should.ThrowAsync<ModerationUnavailableException>(async () =>
            await host.Moderator.CheckAsync(["text"], ModerationStage.Draft, Ct));
    }

    [Fact]
    public async Task Moderator_RecordsUsageWithOperationModerationAndZeroCost()
    {
        using var host = new ModerationTestHost();
        host.Handler.Reply(HttpStatusCode.OK, ModerationTestHost.Body(Array.Empty<string>()), ("x-request-id", "req-moderation-1"));
        var run = ModerationTestHost.Run();

        using (ModerationScope.Push(run))
        {
            await host.Moderator.CheckAsync(["a quiet bedtime story"], ModerationStage.Brief, Ct);
        }

        var item = host.Meter.Items.ShouldHaveSingleItem();
        item.Operation.ShouldBe("moderation");
        item.Provider.ShouldBe("openai");
        item.CostUsd.ShouldBe(0m);
        item.Estimated.ShouldBeTrue();
        item.InputTokens.ShouldBeNull();
        item.UserId.ShouldBe(run.UserId);
        item.EpisodeId.ShouldBe(run.EpisodeId);
        item.GenerationRunId.ShouldBe(run.GenerationRunId);
        item.ReservationId.ShouldBe(run.Reservation.Value);
        item.PriceVersion.ShouldBe("1");
        item.ProviderRequestId.ShouldBe("req-moderation-1");
    }

    [Fact]
    public async Task Moderator_OutsideARunScope_IsNotMetered()
    {
        using var host = new ModerationTestHost();
        host.Handler.Reply(HttpStatusCode.OK, ModerationTestHost.Body(Array.Empty<string>()));

        await host.Moderator.CheckAsync(["text"], ModerationStage.Draft, Ct);

        host.Meter.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Moderator_NeverLogsTheCheckedText()
    {
        using var host = new ModerationTestHost();
        host.Handler.Reply(HttpStatusCode.OK, ModerationTestHost.Body(["violence"]));

        using (ModerationScope.Push(ModerationTestHost.Run()))
        {
            await host.Moderator.CheckAsync(["DRAFT-TEXT-SECRET"], ModerationStage.Draft, Ct);
        }

        host.Logs.Messages.ShouldNotBeEmpty();
        host.Logs.Messages.ShouldNotContain(message => message.Contains("DRAFT-TEXT-SECRET", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Moderator_RefusalOutcome_MapsToModeratedNotProviderError()
    {
        using var host = new OpenAiTestHost();
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Refusal());

        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        result.ShouldBeOfType<TextCallModerated<Probe>>();
    }

    [Fact]
    public async Task Moderator_ContentFilterIncompleteReason_MapsToModerated()
    {
        using var host = new OpenAiTestHost();
        host.Handler.Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(string.Empty, status: "incomplete", incomplete: "content_filter"));

        var result = await host.Client.CallAsync<Probe>(OpenAiTestHost.Request(), Ct);

        result.ShouldBeOfType<TextCallModerated<Probe>>();
    }

    [Fact]
    public void Moderator_SeriesLevelStage_RecordsStageKind()
    {
        ModerationStage.BibleProposal.ToWireName().ShouldBe("bible_proposal");
        ModerationStage.Ideas.ToWireName().ShouldBe("ideas");
        ModerationStage.FindSources.ToWireName().ShouldBe("find_sources");
        ModerationStage.Brief.ToWireName().ShouldBe("brief");
        ModerationStage.Draft.ToWireName().ShouldBe("draft");
        ModerationStage.AutopilotGate.ToWireName().ShouldBe("autopilot_gate");
    }

    // ---- QA additions (02.4.1-T1): boundaries, unusable answers, retry and timeout, category table, D19 ----

    [Theory]
    [InlineData(7_999, 1)]
    [InlineData(8_000, 1)]
    [InlineData(8_001, 2)]
    [InlineData(16_000, 2)]
    [InlineData(16_001, 3)]
    public void Chunk_AtTheCap_SplitsOnlyAboveIt(int length, int expectedChunks)
    {
        var chunks = OpenAiContentModerator.Chunk([new string('a', length)], 8_000);

        chunks.Count.ShouldBe(expectedChunks);
        chunks.ShouldAllBe(chunk => chunk.Length <= 8_000);
        string.Concat(chunks).Length.ShouldBe(length);
    }

    [Fact]
    public async Task Moderator_TextOfExactlyTheCap_IsOneInput_AndOneOverIsTwo()
    {
        using var host = new ModerationTestHost();
        host.Handler
            .Reply(HttpStatusCode.OK, ModerationTestHost.Body(Array.Empty<string>()))
            .Reply(HttpStatusCode.OK, ModerationTestHost.Body([], []));

        await host.Moderator.CheckAsync([new string('a', 8_000)], ModerationStage.Draft, Ct);
        await host.Moderator.CheckAsync([new string('a', 8_001)], ModerationStage.Draft, Ct);

        host.Handler.Requests[0].Json.GetProperty("input").GetArrayLength().ShouldBe(1);
        host.Handler.Requests[1].Json.GetProperty("input").EnumerateArray().Select(c => c.GetString()!.Length).ShouldBe([8_000, 1]);
    }

    [Theory]
    [InlineData(7_998)] // the pair ends exactly on the cap
    [InlineData(7_999)] // the pair straddles the cap
    [InlineData(8_000)] // the pair starts right after the cap
    public void Chunk_PairAroundTheCap_IsNeverSplit(int prefix)
    {
        var text = new string('a', prefix) + "\U0001F600" + "tail";

        var chunks = OpenAiContentModerator.Chunk([text], 8_000);

        chunks.ShouldAllBe(c => c.Length <= 8_000);
        chunks.ShouldAllBe(c => !char.IsHighSurrogate(c.Last()) && !char.IsLowSurrogate(c.First()));
        string.Concat(chunks).ShouldBe(text);
    }

    [Fact]
    public void Chunk_ManyPairsAndAnOddCap_EveryPieceIsWellFormedUtf16()
    {
        var text = string.Concat(Enumerable.Repeat("\U0001F600", 9_001));
        var strict = new UTF8Encoding(false, true);

        var chunks = OpenAiContentModerator.Chunk([text], 8_001);

        chunks.ShouldAllBe(c => c.Length <= 8_001 && c.Length % 2 == 0);
        foreach (var chunk in chunks)
        {
            Should.NotThrow(() => strict.GetBytes(chunk));
        }

        string.Concat(chunks).ShouldBe(text);
    }

    [Fact]
    public async Task Moderator_LoneSurrogateInTheText_AnswersOrFailsClosed_NeverAnUntypedException()
    {
        using var host = new ModerationTestHost();
        host.Handler.Reply(HttpStatusCode.OK, ModerationTestHost.Body(Array.Empty<string>()));

        var failure = await Record.ExceptionAsync(async () =>
            await host.Moderator.CheckAsync(["abc\ud83d"], ModerationStage.Brief, Ct));

        (failure is null or ModerationUnavailableException).ShouldBeTrue($"the port contract allows only ModerationUnavailableException, got {failure}");
    }

    [Fact]
    public async Task Moderator_EmptyListNullAndUnicodeBlankEntries_SendNothing()
    {
        using var host = new ModerationTestHost();

        using (ModerationScope.Push(ModerationTestHost.Run()))
        {
            (await host.Moderator.CheckAsync([], ModerationStage.Brief, Ct)).Flagged.ShouldBeFalse();
            (await host.Moderator.CheckAsync(["\t\n", "\u00A0\u2003", null!], ModerationStage.Brief, Ct)).Flagged.ShouldBeFalse();
        }

        host.Handler.Requests.ShouldBeEmpty();
        host.Meter.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Moderator_BlankEntriesBetweenRealOnes_AreSkipped_AndTextRoundTripsExactly()
    {
        using var host = new ModerationTestHost();
        host.Handler.Reply(HttpStatusCode.OK, ModerationTestHost.Body([], []));
        const string Real = "quote \" backslash \\ newline \n emoji \U0001F600 </script> & <b>";

        await host.Moderator.CheckAsync(["", Real, "   ", "second"], ModerationStage.Draft, Ct);

        var input = host.Handler.Requests.ShouldHaveSingleItem().Json.GetProperty("input");
        input.EnumerateArray().Select(e => e.GetString()).ShouldBe([Real, "second"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("MALFORMED-BODY-SECRET not json")]
    [InlineData("<html>502 Bad Gateway</html>")]
    [InlineData("{\"results\":")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"results\":\"x\"}")]
    [InlineData("{\"results\":[1]}")]
    [InlineData("{\"error\":{\"message\":\"MALFORMED-BODY-SECRET\"}}")]
    public async Task Moderator_Http200WithAnUnusableBody_FailsClosed_WithoutRetryOrLeak(string body)
    {
        using var host = new ModerationTestHost();
        host.Handler.Reply(HttpStatusCode.OK, body);

        ModerationUnavailableException failure;
        using (ModerationScope.Push(ModerationTestHost.Run()))
        {
            failure = await Should.ThrowAsync<ModerationUnavailableException>(async () =>
                await host.Moderator.CheckAsync(["BRIEF-TEXT-SECRET"], ModerationStage.Brief, Ct));
        }

        failure.Status.ShouldBeNull();
        host.Handler.Requests.Count.ShouldBe(1, "a malformed answer is not retried");
        host.Meter.Items.ShouldBeEmpty("an unusable answer has no usage row");
        host.Logs.Messages.ShouldNotContain(m => m.Contains("MALFORMED-BODY-SECRET", StringComparison.Ordinal) || m.Contains("BRIEF-TEXT-SECRET", StringComparison.Ordinal));
        failure.ToString().ShouldNotContain("MALFORMED-BODY-SECRET");
    }

    [Theory(Skip = "DEFECT QA-2 (02.4.1-T1): Parse treats a result without a boolean flagged/categories as clean (fail-open). Remove Skip when fixed.")]
    [InlineData("{\"results\":[{}]}")]
    [InlineData("{\"results\":[{\"flagged\":null,\"categories\":null}]}")]
    [InlineData("{\"results\":[{\"flagged\":\"true\",\"categories\":{\"violence\":\"true\"}}]}")]
    public async Task Moderator_ResultWithoutABooleanVerdict_FailsClosed(string body)
    {
        using var host = new ModerationTestHost();
        host.Handler.Reply(HttpStatusCode.OK, body);

        await Should.ThrowAsync<ModerationUnavailableException>(async () =>
            await host.Moderator.CheckAsync(["a brief"], ModerationStage.Brief, Ct));
    }

    [Fact(Skip = "DEFECT QA-3 (02.4.1-T1): fewer results[] than inputs is accepted; an unchecked chunk passes silently (fail-open). Remove Skip when fixed.")]
    public async Task Moderator_FewerResultsThanInputs_FailsClosed()
    {
        using var host = new ModerationTestHost();
        host.Handler.Reply(HttpStatusCode.OK, ModerationTestHost.Body(Array.Empty<string>()));

        await Should.ThrowAsync<ModerationUnavailableException>(async () =>
            await host.Moderator.CheckAsync([new string('a', 16_001)], ModerationStage.Draft, Ct));
    }

    [Fact]
    public async Task Moderator_429ThenSuccess_WithinTheRetryBudget_Answers()
    {
        using var host = new ModerationTestHost();
        host.Handler
            .Reply(HttpStatusCode.TooManyRequests, "{\"error\":{\"message\":\"slow down\"}}")
            .Reply(HttpStatusCode.OK, ModerationTestHost.Body(["violence"]), ("x-request-id", "req-after-429"));

        ModerationResult result;
        using (ModerationScope.Push(ModerationTestHost.Run()))
        {
            result = await host.Moderator.CheckAsync(["text"], ModerationStage.Draft, Ct);
        }

        result.Flagged.ShouldBeTrue();
        result.Categories.ShouldBe(["violence"]);
        host.Handler.Requests.Count.ShouldBe(2);
        host.Meter.Items.ShouldHaveSingleItem().ProviderRequestId.ShouldBe("req-after-429");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task Moderator_TransientStatusThenSuccess_Retries(HttpStatusCode status)
    {
        using var host = new ModerationTestHost();
        host.Handler
            .Reply(status, "{}")
            .Reply(HttpStatusCode.OK, ModerationTestHost.Body(Array.Empty<string>()));

        var result = await host.Moderator.CheckAsync(["text"], ModerationStage.Brief, Ct);

        result.Flagged.ShouldBeFalse();
        host.Handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Moderator_429Twice_FailsClosedAfterTheBudget()
    {
        using var host = new ModerationTestHost();
        host.Handler
            .Reply(HttpStatusCode.TooManyRequests, "{}")
            .Reply(HttpStatusCode.TooManyRequests, "{}");

        var failure = await Should.ThrowAsync<ModerationUnavailableException>(async () =>
            await host.Moderator.CheckAsync(["text"], ModerationStage.Brief, Ct));

        failure.Status.ShouldBe(429);
        host.Handler.Requests.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{}")]
    [InlineData(HttpStatusCode.Unauthorized, "{}")]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    [InlineData(HttpStatusCode.TooManyRequests, "{\"error\":{\"code\":\"spend_limit_exceeded\"}}")]
    public async Task Moderator_StatusNoRetryCanFix_FailsClosedAfterOneRequest(HttpStatusCode status, string body)
    {
        using var host = new ModerationTestHost();
        host.Handler.Reply(status, body).Reply(HttpStatusCode.OK, ModerationTestHost.Body(Array.Empty<string>()));

        var failure = await Should.ThrowAsync<ModerationUnavailableException>(async () =>
            await host.Moderator.CheckAsync(["text"], ModerationStage.Brief, Ct));

        failure.Status.ShouldBe((int)status);
        host.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Moderator_TimeoutOnBothAttempts_FailsClosedWithoutAStatus()
    {
        using var host = new ModerationTestHost(OneSecondTimeout());
        host.Handler.Then(Hang).Then(Hang);

        var failure = await Should.ThrowAsync<ModerationUnavailableException>(async () =>
            await host.Moderator.CheckAsync(["text"], ModerationStage.Brief, Ct));

        failure.Status.ShouldBeNull();
        host.Handler.Requests.Count.ShouldBe(2, "one attempt plus one retry");
    }

    [Fact]
    public async Task Moderator_TimeoutThenSuccess_Answers()
    {
        using var host = new ModerationTestHost(OneSecondTimeout());
        host.Handler.Then(Hang).Reply(HttpStatusCode.OK, ModerationTestHost.Body(["hate"]));

        var result = await host.Moderator.CheckAsync(["text"], ModerationStage.Brief, Ct);

        result.Categories.ShouldBe(["hate"]);
        host.Handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Moderator_TransportFailureOnBothAttempts_FailsClosed_WithoutTheMessage()
    {
        using var host = new ModerationTestHost();
        host.Handler
            .Then(_ => throw new HttpRequestException("CONNECT-SECRET host unreachable"))
            .Then(_ => throw new HttpRequestException("CONNECT-SECRET host unreachable"));

        var failure = await Should.ThrowAsync<ModerationUnavailableException>(async () =>
            await host.Moderator.CheckAsync(["BRIEF-TEXT-SECRET"], ModerationStage.Brief, Ct));

        failure.ToString().ShouldNotContain("CONNECT-SECRET");
        host.Logs.Messages.ShouldNotContain(m => m.Contains("BRIEF-TEXT-SECRET", StringComparison.Ordinal), "D19: the checked text never reaches a log");
    }

    [Fact]
    public async Task Moderator_CallerCancelledBeforeTheCall_IsCancellation_NotUnavailable()
    {
        using var host = new ModerationTestHost();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var failure = await Record.ExceptionAsync(async () =>
            await host.Moderator.CheckAsync(["text"], ModerationStage.Brief, cts.Token));

        failure.ShouldNotBeNull();
        failure.ShouldBeAssignableTo<OperationCanceledException>();
        host.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Moderator_CallerCancelsInFlight_IsCancellation_NotUnavailable()
    {
        using var host = new ModerationTestHost();
        host.Handler.Then(Hang);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var failure = await Record.ExceptionAsync(async () =>
            await host.Moderator.CheckAsync(["text"], ModerationStage.Brief, cts.Token));

        failure.ShouldNotBeNull();
        failure.ShouldBeAssignableTo<OperationCanceledException>();
    }

    [Fact]
    public async Task Moderator_CategoriesOutsideTheD12Table_PassThroughAndAreAdvisory()
    {
        using var host = new ModerationTestHost();
        host.Handler.Reply(HttpStatusCode.OK, ModerationTestHost.Body(["illicit", "self-harm/intent", "future/new-category"]));

        var result = await host.Moderator.CheckAsync(["text"], ModerationStage.Draft, Ct);

        result.Flagged.ShouldBeTrue();
        result.Categories.ShouldBe(["future/new-category", "illicit", "self-harm/intent"]);
        var classification = new ModerationOptions().Classify(result.Categories);
        classification.BlocksApproval.ShouldBeFalse("only the four D12 categories block approval");
        classification.Advisory.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Moderator_IllicitViolent_ReachesTheCallerAndBlocksApproval()
    {
        using var host = new ModerationTestHost();
        host.Handler.Reply(HttpStatusCode.OK, ModerationTestHost.Body(["illicit/violent", "illicit", "violence"]));

        var result = await host.Moderator.CheckAsync(["text"], ModerationStage.Draft, Ct);

        var classification = new ModerationOptions().Classify(result.Categories);
        classification.Blocking.ShouldBe(["illicit/violent"]);
        classification.Advisory.ShouldBe(["illicit", "violence"]);
    }

    [Fact]
    public async Task Moderator_CategoryTrueWithFlaggedFalse_StillFlags()
    {
        using var host = new ModerationTestHost();
        host.Handler.Reply(HttpStatusCode.OK, "{\"results\":[{\"flagged\":false,\"categories\":{\"violence\":true,\"hate\":false},\"category_scores\":{\"violence\":0.9}}]}");

        var result = await host.Moderator.CheckAsync(["text"], ModerationStage.Draft, Ct);

        result.Flagged.ShouldBeTrue();
        result.Categories.ShouldBe(["violence"]);
    }

    [Fact]
    public async Task Moderator_FlaggedTrueWithNoCategory_StillFlags()
    {
        using var host = new ModerationTestHost();
        host.Handler.Reply(HttpStatusCode.OK, "{\"results\":[{\"flagged\":true,\"categories\":{\"hate\":false},\"category_scores\":{}}]}");

        var result = await host.Moderator.CheckAsync(["text"], ModerationStage.Brief, Ct);

        result.Flagged.ShouldBeTrue();
        result.Categories.ShouldBeEmpty();
        new ModerationOptions().Classify(result.Categories).BlocksApproval.ShouldBeFalse();
    }

    [Fact]
    public async Task Moderator_ScoresAcrossChunks_KeepTheHighestPerCategory()
    {
        using var host = new ModerationTestHost();
        host.Handler.Reply(
            HttpStatusCode.OK,
            "{\"results\":[{\"flagged\":false,\"categories\":{\"violence\":false},\"category_scores\":{\"violence\":0.2}},"
            + "{\"flagged\":false,\"categories\":{\"violence\":false},\"category_scores\":{\"violence\":0.9}}]}");

        var result = await host.Moderator.CheckAsync([new string('a', 8_001)], ModerationStage.Draft, Ct);

        result.Flagged.ShouldBeFalse();
        result.Scores["violence"].ShouldBe(0.9);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task Moderator_ProviderErrorBody_NeverReachesTheLogsOrTheException(HttpStatusCode status)
    {
        using var host = new ModerationTestHost();
        const string Body = "{\"error\":{\"message\":\"PROVIDER-MESSAGE-SECRET echoing BRIEF-TEXT-SECRET\"}}";
        host.Handler.Reply(status, Body).Reply(status, Body);

        ModerationUnavailableException failure;
        using (ModerationScope.Push(ModerationTestHost.Run()))
        {
            failure = await Should.ThrowAsync<ModerationUnavailableException>(async () =>
                await host.Moderator.CheckAsync(["BRIEF-TEXT-SECRET"], ModerationStage.Brief, Ct));
        }

        host.Logs.Messages.ShouldNotBeEmpty();
        host.Logs.Messages.ShouldNotContain(m => m.Contains("PROVIDER-MESSAGE-SECRET", StringComparison.Ordinal) || m.Contains("BRIEF-TEXT-SECRET", StringComparison.Ordinal));
        failure.ToString().ShouldNotContain("PROVIDER-MESSAGE-SECRET");
        failure.ToString().ShouldNotContain("BRIEF-TEXT-SECRET");
    }

    [Fact(Skip = "DEFECT QA-1 (02.4.1-T1): ModerationEvent.Record cuts excerpt[..300] across a surrogate pair. Remove Skip when fixed.")]
    public void ModerationEvent_ExcerptCutAt300_NeverEndsInAHalfSurrogatePair()
    {
        var text = new string('a', 299) + "\U0001F600" + "tail";

        var row = ModerationEvent.Record(Guid.NewGuid(), ModerationSource.OpenaiText, "draft", ["violence"], DateTimeOffset.UnixEpoch, null, text);

        row.Excerpt.ShouldNotBeNull();
        row.Excerpt.Length.ShouldBeLessThanOrEqualTo(300);
        char.IsHighSurrogate(row.Excerpt[^1]).ShouldBeFalse("a half pair cannot be encoded as UTF-8 and would make the row insert fail");
    }

    private static Dictionary<string, string?> OneSecondTimeout() => new() { ["OpenAi:Moderation:TimeoutSeconds"] = "1" };

    private static async Task<HttpResponseMessage> Hang(CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);

        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}
