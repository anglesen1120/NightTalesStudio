// The Responses API of OpenAI 2.14.0 is marked experimental (OPENAI001); the adapter under test uses it.
#pragma warning disable OPENAI001

using System.Net;
using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Text;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Features.Stories.Writing;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Generation;
using NightTales.Domain.Stories.Length;
using NightTales.Infrastructure.Prompts;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.OpenAI;

/// <summary>
/// 02.2.1-T2: the writer over the real adapter, assembler and embedded catalog. <c>GenerateStoryJob</c> (02.2.2-T3)
/// does not exist yet, so the invalid-then-valid handshake is driven at the writer boundary the job will use: the
/// second request carries the corrective note's rule ids and never the first draft's text.
/// </summary>
public sealed class OpenAiStoryWriterRetryTests
{
    private const string FirstDraftMarker = "FIRST-DRAFT-MARKER-TEXT";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OpenAiStoryWriter_InvalidThenValid_SecondRequestCarriesRuleIdsAndNotTheFirstDraft()
    {
        using var host = new OpenAiTestHost();
        var catalog = new PromptCatalog(new EmbeddedPromptResourceSource());
        var writer = new OpenAiStoryWriter(
            new PromptAssembler(catalog, NullLogger<PromptAssembler>.Instance),
            host.Client,
            catalog,
            new RecordingMeter(),
            new FixedEstimator());
        host.Handler
            .Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(FirstDraftJson(FirstDraftMarker)))
            .Reply(HttpStatusCode.OK, OpenAiTestHost.Completed(FirstDraftJson("second draft")));

        var first = await writer.WriteAsync(Request(attempt: 1, violated: null), Ct);
        var second = await writer.WriteAsync(Request(attempt: 2, violated: ["length_out_of_range", "keywords_missing"]), Ct);

        first.ShouldBeOfType<TextCallOk<StoryContentV1>>();
        second.ShouldBeOfType<TextCallOk<StoryContentV1>>();

        var bodies = host.Handler.Requests.Select(r => r.Body).ToArray();
        bodies.Length.ShouldBe(2);
        bodies[0].ShouldNotContain("corrective_note");

        var userData = host.Handler.Requests[1].Json.GetProperty("input")[1].ToString();
        userData.ShouldContain("corrective_note");
        userData.ShouldContain("violated_rules");
        userData.ShouldContain("length_out_of_range");
        userData.ShouldContain("keywords_missing");
        bodies[1].ShouldNotContain(FirstDraftMarker);
        host.Handler.Requests[1].Headers["X-Client-Request-Id"].ShouldEndWith(":writer:2");
    }

    private static string FirstDraftJson(string title) =>
        """
        {"title":"TITLE","hook":{"text":"h","keywords":["door"],"fact_refs":[]},
         "paragraphs":[{"text":"p","keywords":["hall"],"fact_refs":[],"beat":"setup"}],
         "ending":{"text":"e","keywords":["night"],"fact_refs":[]},
         "description":"d","tags":["ghost"],"content_warnings":[],
         "structure":{"sub_genre":"haunted_house","setting":"house","opening":"cold","twist_type":"reveal","narrator_mode":"first"}}
        """.Replace("TITLE", title, StringComparison.Ordinal);

    private static StoryWriteRequest Request(int attempt, IReadOnlyList<string>? violated) =>
        new(
            new RunContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new ReservationId(Guid.NewGuid()), GenerationTrigger.User, attempt, "1"),
            new SeriesSnapshot(Guid.NewGuid(), Topic.Ghost, Language.Vi),
            new EpisodeSnapshot(Guid.NewGuid(), "A haunted house at night", LengthTier.Standard, null),
            new LengthTarget(1300, 2080, 20, 32, 6000, 2500, 6000),
            null,
            null,
            [],
            violated);

    private sealed class RecordingMeter : IUsageMeter
    {
        public Task RecordAsync(UsageItem item, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FixedEstimator : IStoryCostEstimator
    {
        public Task<ErrorOr<UsageBound>> WorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(new UsageBound(1m, 1, 1));

        public Task<ErrorOr<UsageBound>> WriterWorstCaseAsync(LengthTier tier, Language language, CancellationToken ct) =>
            Task.FromResult<ErrorOr<UsageBound>>(new UsageBound(1m, 1, 1));
    }
}
