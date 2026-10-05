using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Features.Stories.Moderation;
using NightTales.Domain.Compliance;
using NightTales.TestKit.Moderation;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Stories.Moderation;

/// <summary>Deterministic D12 edit/approval checks; no provider transport or generation scope.</summary>
public sealed class StoryDraftModerationTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid EpisodeId = Guid.NewGuid();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Clean_SelectsNonblankDraftTexts_AndWritesFreshVerdictWithoutEvent()
    {
        var moderator = new FakeContentModerator();
        var events = new RecordingEvents();
        var helper = Helper(moderator, events);

        var verdict = await helper.CheckAsync(UserId, EpisodeId, "title", "body", "  ", ["tag", "", " \t"], Ct);

        verdict.ShouldNotBeNull();
        JsonNode.DeepEquals(verdict, JsonNode.Parse(
            """{"flagged":false,"categories":[],"blocking":[],"stale":false,"unavailable":false}""")).ShouldBeTrue();
        moderator.Calls.ShouldBe(1);
        moderator.Checks[0].Stage.ShouldBe(ModerationStage.Draft);
        moderator.Checks[0].Texts.ShouldBe(["title", "body", "tag"]);
        events.Items.ShouldBeEmpty();
        StoryDraftModeration.NeedsRecheck(verdict).ShouldBeFalse();
        StoryDraftModeration.BlockingCategoriesOf(verdict).ShouldBeEmpty();
    }

    [Fact]
    public async Task Check_SelectsDescriptionBeforeTags_OutsideModerationScope()
    {
        var moderator = new StubModerator((texts, stage, ct) =>
        {
            ModerationScope.Current.ShouldBeNull();
            texts.ShouldBe(["title", "body", "description", "first", "second"]);
            stage.ShouldBe(ModerationStage.Draft);
            ct.ShouldBe(Ct);
            return ValueTask.FromResult(ModerationResult.Clean);
        });

        (await CheckAsync(Helper(moderator, new RecordingEvents()))).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("sexual/minors", "sexual/minors")]
    [InlineData("self-harm/instructions", "self-harm/instructions")]
    [InlineData("illicit/violent", "illicit/violent")]
    [InlineData("hate/threatening", "hate/threatening")]
    [InlineData("violence", "")]
    public async Task Flagged_RecordsEvent_AndOnlyD12CategoriesBlock(string category, string expectedBlocking)
    {
        var moderator = new FakeContentModerator().EnqueueFlagged(ModerationStage.Draft, category);
        var events = new RecordingEvents();

        var verdict = await CheckAsync(Helper(moderator, events));

        verdict.ShouldNotBeNull();
        verdict["flagged"]!.GetValue<bool>().ShouldBeTrue();
        verdict["categories"]!.AsArray().Select(node => node!.GetValue<string>()).ShouldBe([category]);
        StoryDraftModeration.BlockingCategoriesOf(verdict)
            .ShouldBe(expectedBlocking.Length == 0 ? Array.Empty<string>() : [expectedBlocking]);
        StoryDraftModeration.NeedsRecheck(verdict).ShouldBeFalse();
        events.Items.Count.ShouldBe(1);
        var item = events.Items[0];
        item.UserId.ShouldBe(UserId);
        item.EpisodeId.ShouldBe(EpisodeId);
        item.Source.ShouldBe(ModerationSource.OpenaiText);
        item.Stage.ShouldBe(ModerationStage.Draft);
        item.Categories.ShouldBe([category]);
        item.OwnText.ShouldBeNull("draft-stage evidence never copies a stored story into the excerpt");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ContradictoryProviderFlag_IsNotConvertedToClean(bool flagged)
    {
        var result = new ModerationResult(flagged, flagged ? [] : ["sexual/minors"], new Dictionary<string, double>());
        var events = new RecordingEvents();
        var moderator = new StubModerator((_, _, _) => ValueTask.FromResult(result));

        var verdict = await CheckAsync(Helper(moderator, events));

        verdict.ShouldNotBeNull();
        verdict["flagged"]!.GetValue<bool>().ShouldBeTrue();
        StoryDraftModeration.BlockingCategoriesOf(verdict).ShouldBe(flagged ? ["unspecified"] : ["sexual/minors"]);
        events.Items.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UnsafeCategories_AreSanitised_AndCannotBecomeClean(int input)
    {
        // Construct invalid UTF-16 in the test body; custom-attribute metadata cannot reliably retain lone surrogates.
        var (category, cleaned) = input switch
        {
            0 => ("sexual/minors\0", "sexual/minors"),
            1 => ("bad" + '\uD800' + "category", "bad\uFFFDcategory"),
            _ => ("\0", "unspecified"),
        };
        var moderator = new FakeContentModerator().EnqueueFlagged(ModerationStage.Draft, category);
        var events = new RecordingEvents();

        var verdict = await CheckAsync(Helper(moderator, events));

        verdict.ShouldNotBeNull();
        var categories = verdict["categories"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        categories.ShouldContain(cleaned);
        categories.ShouldAllBe(value => !value.Contains('\0') && !value.Any(char.IsSurrogate));
        verdict.ToJsonString().ShouldNotContain("\\u0000");
        StoryDraftModeration.BlockingCategoriesOf(verdict).ShouldContain("unspecified");
        StoryDraftModeration.NeedsRecheck(verdict).ShouldBeFalse();
        events.Items[0].Categories.ShouldBe(categories);
    }

    [Fact]
    public async Task Unavailable_ReturnsNullWithoutEvent()
    {
        var moderator = new FakeContentModerator().EnqueueUnavailable(ModerationStage.Draft);
        var events = new RecordingEvents();

        (await CheckAsync(Helper(moderator, events))).ShouldBeNull();

        moderator.Calls.ShouldBe(1);
        events.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task ProviderTimeoutWithLiveCaller_ReturnsNull()
    {
        var moderator = new StubModerator((_, _, _) => throw new OperationCanceledException("provider deadline"));
        var events = new RecordingEvents();

        (await CheckAsync(Helper(moderator, events))).ShouldBeNull();
        events.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task UnexpectedProviderFailure_ReturnsNull()
    {
        var moderator = new StubModerator((_, _, _) => throw new HttpRequestException("provider failure"));
        var events = new RecordingEvents();

        (await CheckAsync(Helper(moderator, events))).ShouldBeNull();
        events.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task MissingProviderCategories_ReturnsNull_NotClean()
    {
        var moderator = new StubModerator((_, _, _) => ValueTask.FromResult(
            new ModerationResult(false, null!, new Dictionary<string, double>())));

        (await CheckAsync(Helper(moderator, new RecordingEvents()))).ShouldBeNull();
    }

    [Fact]
    public async Task AlreadyCancelledCaller_PropagatesWithoutCallingModerator()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var moderator = new FakeContentModerator();
        var helper = Helper(moderator, new RecordingEvents());

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await helper.CheckAsync(UserId, EpisodeId, "title", "body", null, [], cancellation.Token));

        moderator.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task CallerCancellationDuringProviderCall_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        var moderator = new StubModerator((_, _, ct) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(ct);
        });
        var helper = Helper(moderator, new RecordingEvents());

        var exception = await Should.ThrowAsync<OperationCanceledException>(async () =>
            await helper.CheckAsync(UserId, EpisodeId, "title", "body", null, [], cancellation.Token));

        exception.CancellationToken.ShouldBe(cancellation.Token);
    }

    [Theory]
    [InlineData(null, true, "unspecified")]
    [InlineData("[]", true, "unspecified")]
    [InlineData("{}", true, "unspecified")]
    [InlineData("{\"stale\":true,\"unavailable\":true}", true, "unspecified")]
    [InlineData("{\"flagged\":false,\"categories\":[],\"blocking\":[],\"stale\":false,\"unavailable\":false}", false, "")]
    [InlineData("{\"flagged\":false,\"categories\":[],\"blocking\":[],\"stale\":true,\"unavailable\":false}", true, "")]
    [InlineData("{\"flagged\":false,\"categories\":[],\"blocking\":[],\"stale\":false,\"unavailable\":true}", true, "unspecified")]
    [InlineData("{\"flagged\":true,\"categories\":[\"violence\"],\"blocking\":[],\"stale\":false,\"unavailable\":false}", false, "")]
    [InlineData("{\"flagged\":true,\"categories\":[\"sexual/minors\"],\"blocking\":[\"sexual/minors\"],\"stale\":false,\"unavailable\":false}", false, "sexual/minors")]
    [InlineData("{\"flagged\":true,\"categories\":[\"sexual/minors\"],\"blocking\":[\"sexual/minors\"],\"stale\":true,\"unavailable\":false}", true, "sexual/minors")]
    [InlineData("{\"flagged\":true,\"categories\":[],\"blocking\":[],\"stale\":false,\"unavailable\":false}", true, "unspecified")]
    [InlineData("{\"flagged\":false,\"categories\":[\"sexual/minors\"],\"blocking\":[],\"stale\":false,\"unavailable\":false}", true, "unspecified")]
    [InlineData("{\"flagged\":true,\"categories\":[\"sexual/minors\"],\"blocking\":[],\"stale\":false,\"unavailable\":false}", true, "unspecified")]
    [InlineData("{\"flagged\":\"false\",\"categories\":[],\"blocking\":[],\"stale\":false,\"unavailable\":false}", true, "unspecified")]
    [InlineData("{\"flagged\":false,\"categories\":[3],\"blocking\":[],\"stale\":false,\"unavailable\":false}", true, "unspecified")]
    [InlineData("{\"flagged\":false,\"categories\":[null],\"blocking\":[],\"stale\":false,\"unavailable\":false}", true, "unspecified")]
    [InlineData("{\"flagged\":true,\"categories\":[\"\"],\"blocking\":[],\"stale\":false,\"unavailable\":false}", true, "unspecified")]
    [InlineData("{\"flagged\":false,\"categories\":[],\"blocking\":[\"sexual/minors\"],\"stale\":false,\"unavailable\":false}", true, "unspecified")]
    [InlineData("{\"flagged\":false,\"categories\":[],\"blocking\":[],\"stale\":\"false\",\"unavailable\":false}", true, "unspecified")]
    [InlineData("{\"flagged\":false,\"categories\":[],\"blocking\":[],\"stale\":false,\"unavailable\":\"false\"}", true, "unspecified")]
    [InlineData("{\"flagged\":true,\"categories\":\"sexual/minors\",\"blocking\":[],\"stale\":false,\"unavailable\":false}", true, "unspecified")]
    [InlineData("{\"flagged\":true,\"categories\":[\"sexual/minors\"],\"blocking\":[null],\"stale\":false,\"unavailable\":false}", true, "unspecified")]
    public void StoredVerdict_TruthTable(string? json, bool recheck, string expectedBlocking)
    {
        var moderation = json is null ? null : JsonNode.Parse(json);

        StoryDraftModeration.NeedsRecheck(moderation).ShouldBe(recheck);
        StoryDraftModeration.BlockingCategoriesOf(moderation)
            .ShouldBe(expectedBlocking.Length == 0 ? Array.Empty<string>() : [expectedBlocking]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsafeStoredCategory_IsMalformed_NotAnAdvisoryVerdict(bool surrogate)
    {
        var category = "violence" + (surrogate ? '\uD800' : '\0');
        var verdict = new JsonObject
        {
            ["flagged"] = true,
            ["categories"] = new JsonArray(category),
            ["blocking"] = new JsonArray(),
            ["stale"] = false,
            ["unavailable"] = false,
        };

        StoryDraftModeration.NeedsRecheck(verdict).ShouldBeTrue();
        StoryDraftModeration.BlockingCategoriesOf(verdict).ShouldBe(["unspecified"]);
    }

    private static StoryDraftModeration Helper(IContentModerator moderator, RecordingEvents events) =>
        new(moderator, events, NullLogger<StoryDraftModeration>.Instance);

    private static ValueTask<JsonObject?> CheckAsync(StoryDraftModeration helper) =>
        helper.CheckAsync(UserId, EpisodeId, "title", "body", "description", ["first", "second"], Ct);

    private sealed class StubModerator(
        Func<IReadOnlyList<string>, ModerationStage, CancellationToken, ValueTask<ModerationResult>> check) : IContentModerator
    {
        public ValueTask<ModerationResult> CheckAsync(IReadOnlyList<string> texts, ModerationStage stage, CancellationToken ct) =>
            check(texts, stage, ct);
    }

    private sealed record Event(
        Guid UserId, Guid? EpisodeId, ModerationSource Source, ModerationStage Stage, string[] Categories, string? OwnText);

    private sealed class RecordingEvents : IModerationEventWriter
    {
        public List<Event> Items { get; } = [];

        public Task WriteAsync(
            Guid userId, Guid? episodeId, ModerationSource source, ModerationStage stage,
            IReadOnlyCollection<string> categories, string? ownText, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Items.Add(new Event(userId, episodeId, source, stage, categories.ToArray(), ownText));
            return Task.CompletedTask;
        }
    }
}
