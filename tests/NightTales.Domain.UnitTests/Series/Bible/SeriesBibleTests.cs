using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Structure;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Series.Bible;

/// <summary>
/// The behaviour of the <c>series_bibles</c> aggregate (02.6.3 plan §3.3, §4 U1/U2/U4): the version it owns, the
/// proposal it keeps across a manual save, and the append-only apply that can never store broken content.
/// </summary>
/// <remarks>
/// Pure unit tests: no database, no context. The endpoint contract (ETag, status codes, the derived structure of
/// a summary) belongs to the Application and Api tests of the same task.
/// </remarks>
public sealed class SeriesBibleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 30, 0, TimeSpan.Zero);

    private static readonly Guid SeriesId = Guid.Parse("0197a000-0000-7000-8000-000000000001");
    private static readonly Guid UserId = Guid.Parse("0197a000-0000-7000-8000-000000000002");
    private static readonly Guid EpisodeId = Guid.Parse("0197a000-0000-7000-8000-0000000000a1");

    private static readonly StoryStructureTags Structure = new(
        "haunted_house", "rural_house", "night", "revenant", "first_person", ["setup", "clue", "twist", "reveal"]);

    private static readonly BibleLimits Limits = new();

    [Fact]
    public void CreateDefault_StartsAtVersionOneWithTheEmptyContent()
    {
        var bible = New();

        bible.Version.ShouldBe(SeriesBible.InitialVersion);
        bible.SeriesId.ShouldBe(SeriesId);
        bible.UserId.ShouldBe(UserId);
        bible.Id.ShouldNotBe(Guid.Empty);

        // The very instance, not a copy: the empty bible is immutable and shared by every fresh series.
        bible.Content.ShouldBeSameAs(SeriesBibleContent.Empty);
        bible.ProposedContent.ShouldBeNull();
        bible.ProposedFromEpisodeId.ShouldBeNull();
        bible.CreatedAt.ShouldBe(Now);
        bible.UpdatedAt.ShouldBe(Now);
    }

    [Fact]
    public void CreateDefault_WithoutSeriesOrOwner_Throws()
    {
        Should.Throw<ArgumentException>(() => SeriesBible.CreateDefault(Guid.Empty, UserId, new FakeClock(Now)))
            .ParamName.ShouldBe("seriesId");

        Should.Throw<ArgumentException>(() => SeriesBible.CreateDefault(SeriesId, Guid.Empty, new FakeClock(Now)))
            .ParamName.ShouldBe("userId");
    }

    [Fact]
    public void ReplaceContent_BumpsVersion_AndKeepsProposal()
    {
        var clock = new FakeClock(Now);
        var bible = New();
        bible.SetProposal(Proposal(AddCharacter("Người mới")), EpisodeId, clock);
        var createdAt = bible.CreatedAt;

        clock.Advance(TimeSpan.FromMinutes(5));
        bible.ReplaceContent(Content(characters: [new BibleCharacter("ch1", "Bà Tư", "Người giữ nhà.", null)]), Limits, clock);

        bible.Version.ShouldBe(2);
        bible.UpdatedAt.ShouldBe(clock.UtcNow);
        bible.CreatedAt.ShouldBe(createdAt, "a save never moves the insert instant");
        bible.Content.RecurringCharacters.Count.ShouldBe(1);
        bible.Content.RecurringCharacters[0].Name.ShouldBe("Bà Tư");

        // The proposal survives a manual edit: its ops are append-only, so a save cannot invalidate them (F27).
        bible.ProposedContent.ShouldNotBeNull();
        bible.ProposedContent!.Ops.Count.ShouldBe(1);
        bible.ProposedFromEpisodeId.ShouldBe(EpisodeId);
    }

    [Fact]
    public void ReplaceContent_ForcesTheServerSchemaVersion()
    {
        var bible = New();

        bible.ReplaceContent(
            new SeriesBibleContent("bible.v99", Narrator(), [], [], [], []),
            Limits,
            new FakeClock(Now));

        bible.Content.SchemaVersion.ShouldBe(SeriesBibleContent.V1);
    }

    [Fact]
    public void ReplaceContent_InvalidContent_ThrowsAndLeavesTheRowAsItWas()
    {
        var bible = New();
        var tooMany = Content(characters: Enumerable.Range(1, Limits.MaxCharacters + 1)
            .Select(index => new BibleCharacter($"ch{index}", $"Nhân vật {index}", string.Empty, null))
            .ToList());

        var exception = Should.Throw<ArgumentException>(() => bible.ReplaceContent(tooMany, Limits, new FakeClock(Now)));

        exception.ParamName.ShouldBe("content");
        exception.Message.ShouldContain("recurring_characters");
        exception.Message.ShouldContain(BibleContentValidator.TooMany);
        exception.Message.ShouldNotContain("Nhân vật 31");
        bible.Version.ShouldBe(SeriesBible.InitialVersion);
        bible.Content.ShouldBeSameAs(SeriesBibleContent.Empty);
    }

    [Fact]
    public void ReplaceContent_EmptyBible_IsRefusedByThePersonaRule()
    {
        // The row created with a series holds the empty bible, whose persona is the empty string, while plan §3.2
        // requires a non-empty one: the client's first save supplies it, so a GET followed by an unchanged PUT is
        // answered with 400 `narrator.persona: empty`.
        var bible = New();

        Should.Throw<ArgumentException>(() => bible.ReplaceContent(SeriesBibleContent.Empty, Limits, new FakeClock(Now)));

        bible.Version.ShouldBe(SeriesBible.InitialVersion);
    }

    [Fact]
    public void SetProposal_DoesNotBumpTheVersion()
    {
        var clock = new FakeClock(Now);
        var bible = New();
        clock.Advance(TimeSpan.FromMinutes(1));

        bible.SetProposal(Proposal(AddCharacter("Người mới")), EpisodeId, clock);

        bible.Version.ShouldBe(SeriesBible.InitialVersion, "a proposal is not content");
        bible.UpdatedAt.ShouldBe(clock.UtcNow);
        bible.ProposedContent.ShouldNotBeNull();
        bible.ProposedFromEpisodeId.ShouldBe(EpisodeId);
    }

    [Fact]
    public void SetProposal_WithoutTheEpisode_Throws()
    {
        var bible = New();

        Should.Throw<ArgumentException>(() => bible.SetProposal(Proposal(AddCharacter("Người mới")), Guid.Empty, new FakeClock(Now)))
            .ParamName.ShouldBe("fromEpisodeId");

        bible.ProposedContent.ShouldBeNull();
    }

    [Fact]
    public void ClearProposal_DoesNotBumpTheVersion_AndKeepsTheContent()
    {
        var clock = new FakeClock(Now);
        var bible = New();
        bible.ReplaceContent(Content(), Limits, clock);
        bible.SetProposal(Proposal(AddCharacter("Người mới")), EpisodeId, clock);
        clock.Advance(TimeSpan.FromMinutes(2));

        bible.ClearProposal(clock);

        bible.ProposedContent.ShouldBeNull();
        bible.ProposedFromEpisodeId.ShouldBeNull();
        bible.Version.ShouldBe(2);
        bible.UpdatedAt.ShouldBe(clock.UtcNow);
        bible.Content.Narrator.Persona.ShouldBe(Narrator().Persona);
    }

    [Fact]
    public void ApplyProposal_WithoutAPendingProposal_Throws()
    {
        var bible = New();

        Should.Throw<InvalidOperationException>(() => bible.ApplyProposal(Limits, Source(), new FakeClock(Now)));
    }

    [Fact]
    public void ApplyProposal_AddSummaryForExistingEpisode_SkipsAndKeepsFirst()
    {
        var clock = new FakeClock(Now);
        var bible = New();
        var stored = new BibleEpisodeSummary(EpisodeId, "Đầu tiên", "Bản gốc.", Structure, Now.AddDays(-2));
        bible.ReplaceContent(Content(summaries: [stored]), Limits, clock);
        bible.SetProposal(Proposal(AddSummary(title: "Thứ hai", summary: "Bản thay thế.")), EpisodeId, clock);

        var result = bible.ApplyProposal(Limits, Source(), clock);

        result.Applied.ShouldBeEmpty();
        result.Skipped.ShouldBe(1);
        bible.Version.ShouldBe(3, "version 1 at creation, 2 at the manual save, 3 at the accept");
        bible.Content.EpisodeSummaries.Count.ShouldBe(1);
        bible.Content.EpisodeSummaries[0].Title.ShouldBe("Đầu tiên");
        bible.Content.EpisodeSummaries[0].Summary.ShouldBe("Bản gốc.");
        bible.ProposedContent.ShouldBeNull();
        bible.ProposedFromEpisodeId.ShouldBeNull();
    }

    [Fact]
    public void ApplyProposal_AddSummaryForANewEpisode_UsesTheSourceStructureAndApproval()
    {
        var clock = new FakeClock(Now);
        var bible = New();
        var longSummary = string.Join(' ', Enumerable.Repeat("word", 60));
        bible.SetProposal(
            Proposal(AddSummary(title: new string('t', Limits.SummaryTitleChars + 50), summary: longSummary, reason: "vì sao")),
            EpisodeId,
            clock);
        var source = Source();

        var result = bible.ApplyProposal(Limits, source, clock);
        var summary = bible.Content.EpisodeSummaries.Single();

        result.Applied.Count.ShouldBe(1);
        result.Skipped.ShouldBe(0);
        result.Applied[0].Reason.ShouldBe("vì sao");
        summary.EpisodeId.ShouldBe(EpisodeId);
        summary.Structure.ShouldBeSameAs(Structure, "structure is server-derived, never the client's");
        summary.ApprovedAt.ShouldBe(source.ApprovedAt);

        // Over-long text is truncated on a word boundary on the accept path, never refused (plan §4 U4).
        summary.Title.Length.ShouldBe(Limits.SummaryTitleChars);
        summary.Summary.Length.ShouldBeLessThanOrEqualTo(Limits.SummaryChars);
        longSummary.StartsWith(summary.Summary, StringComparison.Ordinal).ShouldBeTrue();
        char.IsWhiteSpace(longSummary[summary.Summary.Length]).ShouldBeTrue("the cut lands on a word boundary");
    }

    [Fact]
    public void ApplyProposal_AddCharacterWithExistingNormalizedName_Skips()
    {
        var clock = new FakeClock(Now);
        var bible = New();
        bible.ReplaceContent(Content(characters: [new BibleCharacter("ch1", "Bà Tư", string.Empty, null)]), Limits, clock);
        bible.SetProposal(Proposal(AddCharacter(name: "bà  tư.")), EpisodeId, clock);

        var result = bible.ApplyProposal(Limits, Source(), clock);

        result.Applied.ShouldBeEmpty();
        result.Skipped.ShouldBe(1);
        bible.Content.RecurringCharacters.Count.ShouldBe(1);
        bible.Content.RecurringCharacters[0].Id.ShouldBe("ch1");
    }

    [Fact]
    public void ApplyProposal_WithinOneBatchWithTheSameName_AppliesOnlyTheFirst()
    {
        var clock = new FakeClock(Now);
        var bible = New();
        bible.SetProposal(
            Proposal(AddCharacter(name: "Người đàn bà"), AddCharacter(name: "người đàn bà")),
            EpisodeId,
            clock);

        var result = bible.ApplyProposal(Limits, Source(), clock);

        result.Applied.Count.ShouldBe(1);
        result.Skipped.ShouldBe(1);
        bible.Content.RecurringCharacters.Count.ShouldBe(1);
        bible.Content.RecurringCharacters[0].Id.ShouldBe("ch1");
    }

    [Fact]
    public void ApplyProposal_AtCharacterLimit_SkipsOpWithoutThrowing()
    {
        var clock = new FakeClock(Now);
        var bible = New();
        var full = Enumerable.Range(1, Limits.MaxCharacters)
            .Select(index => new BibleCharacter($"ch{index}", $"Nhân vật {index}", string.Empty, null))
            .ToList();
        bible.ReplaceContent(Content(characters: full), Limits, clock);
        bible.SetProposal(Proposal(AddCharacter(name: "Nhân vật mới")), EpisodeId, clock);

        var result = bible.ApplyProposal(Limits, Source(), clock);

        result.Applied.ShouldBeEmpty();
        result.Skipped.ShouldBe(1);
        bible.Content.RecurringCharacters.Count.ShouldBe(Limits.MaxCharacters);
        bible.Version.ShouldBe(3, "the version is bumped once even when every op is skipped");
    }

    [Fact]
    public void ApplyProposal_AssignsSequentialIds_NeverReuses()
    {
        var clock = new FakeClock(Now);
        var bible = New();
        bible.ReplaceContent(
            Content(
                characters: [new BibleCharacter("ch1", "Một", string.Empty, null), new BibleCharacter("ch3", "Ba", string.Empty, null)],
                places: [new BiblePlace("pl2", "Hai", string.Empty)],
                rules: [new BibleRule("rl1", "Luật một")]),
            Limits,
            clock);
        bible.SetProposal(Proposal(AddCharacter(name: "Bốn"), AddPlace(name: "Ba"), AddRule(text: "Luật hai")), EpisodeId, clock);

        var result = bible.ApplyProposal(Limits, Source(), clock);

        result.Applied.Count.ShouldBe(3);
        result.Skipped.ShouldBe(0);
        bible.Content.RecurringCharacters.Select(character => character.Id).ShouldBe(["ch1", "ch3", "ch4"]);
        bible.Content.Places.Select(place => place.Id).ShouldBe(["pl2", "pl3"]);
        bible.Content.Rules.Select(rule => rule.Id).ShouldBe(["rl1", "rl2"]);
    }

    [Fact]
    public void AssignMissingIds_BlankAndAbsentIdsTakeTheNextFreeNumberPerPrefix()
    {
        // The PUT contract (02.6.3-T1): a blank or absent `id` is a new entry, and its id comes from the same
        // `max + 1` rule the append-only ops use — over the request's own ids and the stored content's together,
        // so a kept client id is never handed out again and an id of the stored content is never reused.
        var stored = Content(
            characters: [new BibleCharacter("ch2", "Hai", string.Empty, null)],
            places: [new BiblePlace("pl4", "Bốn", string.Empty)],
            rules: [new BibleRule("rl9", "Chín")]);
        var requested = Content(
            characters: [
                new BibleCharacter("ch7", "Bảy", string.Empty, null),
                new BibleCharacter(string.Empty, "Mới", "Mô tả mới.", null),
                new BibleCharacter("   ", "Mới hơn", string.Empty, null),
            ],
            places: [new BiblePlace(null!, "Nơi mới", string.Empty)],
            rules: [new BibleRule(string.Empty, "Luật mới")]);

        var assigned = SeriesBible.AssignMissingIds(requested, stored);

        assigned.RecurringCharacters.Select(character => character.Id).ShouldBe(["ch7", "ch8", "ch9"]);
        assigned.Places.Select(place => place.Id).ShouldBe(["pl5"]);
        assigned.Rules.Select(rule => rule.Id).ShouldBe(["rl10"]);

        // Only the id moved: the new entry's name and description arrive as the client sent them.
        assigned.RecurringCharacters[1].Name.ShouldBe("Mới");
        assigned.RecurringCharacters[1].Description.ShouldBe("Mô tả mới.");
    }

    [Fact]
    public void AssignMissingIds_NeverReusesAStoredIdTheRequestDropped()
    {
        // ch3 is stored and the request no longer carries it: the blank entry still goes past it, because the
        // highest suffix is taken over the stored content as well (plan §4 U4).
        var stored = Content(characters: [
            new BibleCharacter("ch1", "Một", string.Empty, null),
            new BibleCharacter("ch3", "Ba", string.Empty, null),
        ]);
        var requested = Content(characters: [
            new BibleCharacter("ch1", "Một", string.Empty, null),
            new BibleCharacter(string.Empty, "Mới", string.Empty, null),
        ]);

        var assigned = SeriesBible.AssignMissingIds(requested, stored);

        assigned.RecurringCharacters.Select(character => character.Id).ShouldBe(["ch1", "ch4"]);
    }

    [Fact]
    public void AssignMissingIds_EveryEntryWithAnId_ReturnsTheArraysAsTheyArrived()
    {
        var stored = Content(characters: [new BibleCharacter("ch1", "Một", string.Empty, null)]);
        var requested = Content(
            characters: [new BibleCharacter("ch9", "Chín", string.Empty, null)],
            rules: [new BibleRule("rl1", "Luật")]);

        var assigned = SeriesBible.AssignMissingIds(requested, stored);

        // A kept client id stays verbatim (the validator is what refuses a bad shape), and an untouched array is
        // not copied.
        assigned.RecurringCharacters.ShouldBeSameAs(requested.RecurringCharacters);
        assigned.Places.ShouldBeSameAs(requested.Places);
        assigned.Rules.ShouldBeSameAs(requested.Rules);
    }

    [Fact]
    public void ApplyProposal_HighestIdRemovedByAManualSave_IsAssignedAgain()
    {
        // The behaviour of the plan §4 U4 rule `max(existing numeric suffix) + 1`: an id that was the highest one
        // and was removed by a manual save cannot be told apart from one that was never assigned, so the plan §9
        // row that expects `ch3` after a manual delete of `ch2` needs a high-water counter column that plan §3.3
        // does not define. Pinned here so the deviation is visible; reported as an open gap.
        var clock = new FakeClock(Now);
        var bible = New();
        bible.ReplaceContent(
            Content(characters: [new BibleCharacter("ch1", "Một", string.Empty, null), new BibleCharacter("ch2", "Hai", string.Empty, null)]),
            Limits,
            clock);
        bible.ReplaceContent(
            Content(characters: [new BibleCharacter("ch1", "Một", string.Empty, null)]),
            Limits,
            clock);
        bible.SetProposal(Proposal(AddCharacter(name: "Ba")), EpisodeId, clock);

        bible.ApplyProposal(Limits, Source(), clock);

        bible.Content.RecurringCharacters.Select(character => character.Id).ShouldBe(["ch1", "ch2"]);
    }

    [Fact]
    public void ApplyProposal_NeverTouchesNarrator()
    {
        var clock = new FakeClock(Now);
        var bible = New();
        bible.ReplaceContent(Content(), Limits, clock);
        var narrator = bible.Content.Narrator;
        bible.SetProposal(
            Proposal(AddSummary(), AddCharacter(name: "Mới"), AddPlace(name: "Nơi mới"), AddRule(text: "Luật mới")),
            EpisodeId,
            clock);

        var result = bible.ApplyProposal(Limits, Source(), clock);

        result.Applied.Count.ShouldBe(4);
        result.Skipped.ShouldBe(0);
        bible.Content.Narrator.ShouldBeSameAs(narrator, "no op kind exists for the narrator (§4 U4)");
        bible.Content.EpisodeSummaries.Count.ShouldBe(1);
        bible.Content.RecurringCharacters.Count.ShouldBe(1);
        bible.Content.Places.Count.ShouldBe(1);
        bible.Content.Rules.Count.ShouldBe(1);
        bible.ProposedContent.ShouldBeNull();
    }

    [Fact]
    public void ApplyProposal_BlankOpPayloads_AreSkipped()
    {
        var clock = new FakeClock(Now);
        var bible = New();
        bible.SetProposal(
            Proposal(
                AddSummary(title: "  ", summary: "Nội dung."),
                AddSummary(title: "Tập một", summary: null),
                AddCharacter(name: null),
                AddPlace(name: "   "),
                AddRule(text: string.Empty)),
            EpisodeId,
            clock);

        var result = bible.ApplyProposal(Limits, Source(), clock);

        result.Applied.ShouldBeEmpty();
        result.Skipped.ShouldBe(5);
        bible.Content.EpisodeSummaries.ShouldBeEmpty();
        bible.Content.RecurringCharacters.ShouldBeEmpty();
    }

    [Fact]
    public void ApplyProposal_WouldExceedTheByteBudget_SkipsTheOp()
    {
        var clock = new FakeClock(Now);
        var bible = New();
        var stored = Content(rules: [new BibleRule("rl1", "Luật một")]);
        bible.ReplaceContent(stored, Limits, clock);
        bible.SetProposal(Proposal(AddRule(text: "Luật hai — một câu đủ dài để vượt ngân sách.")), EpisodeId, clock);
        var tight = Limits with { MaxContentBytes = BibleJson.Utf8ByteCount(stored) + 10 };

        var result = bible.ApplyProposal(tight, Source(), clock);

        result.Applied.ShouldBeEmpty();
        result.Skipped.ShouldBe(1);
        bible.Content.Rules.Count.ShouldBe(1);
        bible.Version.ShouldBe(3);
    }

    private static SeriesBible New() => SeriesBible.CreateDefault(SeriesId, UserId, new FakeClock(Now));

    private static BibleNarrator Narrator() => new("Bà Tư", "Kể chuyện đêm khuya cho người lớn.", string.Empty);

    private static SeriesBibleContent Content(
        IReadOnlyList<BibleCharacter>? characters = null,
        IReadOnlyList<BiblePlace>? places = null,
        IReadOnlyList<BibleRule>? rules = null,
        IReadOnlyList<BibleEpisodeSummary>? summaries = null) =>
        new(SeriesBibleContent.V1, Narrator(), characters ?? [], places ?? [], rules ?? [], summaries ?? []);

    /// <summary>
    /// A proposal computed against the initial version: an op list is append-only, so `based_on_version` is
    /// informational and never invalidates it (F27).
    /// </summary>
    private static BibleProposal Proposal(params BibleOp[] ops) => new(ops, SeriesBible.InitialVersion, Now);

    private static BibleSummarySource Source() => new(EpisodeId, Structure, Now.AddDays(-1));

    private static BibleOp AddSummary(string? title = "Tập một", string? summary = "Một câu chuyện.", string? reason = null) =>
        new(BibleOpKind.AddSummary, summary, title, null, null, reason);

    private static BibleOp AddCharacter(string? name = "Bà Tư", string? description = "Người giữ nhà.") =>
        new(BibleOpKind.AddCharacter, null, name, description, null, null);

    private static BibleOp AddPlace(string? name = "Căn nhà bên sông") =>
        new(BibleOpKind.AddPlace, null, name, "Nhà gỗ hai tầng.", null, null);

    private static BibleOp AddRule(string? text = "Không nói tên thật.") =>
        new(BibleOpKind.AddRule, null, null, null, text, null);
}
