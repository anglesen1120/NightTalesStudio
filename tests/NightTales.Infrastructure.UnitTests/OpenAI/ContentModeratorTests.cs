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

    internal ModerationTestHost()
    {
        Handler = new ScriptedHandler();
        Logs = new CapturingLoggerProvider();
        Meter = new RecordingUsageMeter();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
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
}
