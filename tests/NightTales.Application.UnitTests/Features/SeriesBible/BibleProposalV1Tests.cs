using System.Text.Json;
using NightTales.Application.Features.SeriesBible;
using NightTales.Domain.Series.Bible;
using NightTales.TestKit.Fixtures;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.SeriesBible;

/// <summary>
/// The <c>bible-proposal.v1</c> model answer of 02.6.3-T2 (plan §7.1 step 5): the DTO the adapter deserialises
/// into, the mapping to the Domain candidates <see cref="BibleProposalValidator"/> consumes, and the injection
/// fixtures the prompt ships beside its schema (plan §8).
/// </summary>
public sealed class BibleProposalV1Tests
{
    /// <summary>
    /// The options the adapter deserialises model output with, mirroring <c>OpenAiTextClient.OutputOptions</c>
    /// (snake_case wire names, nullable annotations and required constructor parameters respected): the DTO must
    /// round-trip under exactly this policy, because that is the only deserialiser it is ever read by.
    /// </summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    /// <summary>The bible-step fixtures the prompt ships, ordered by id.</summary>
    private static readonly string[] BibleFixtureIds =
    [
        "bible-forged-control-keys",
        "bible-ignore-instructions-vi",
    ];

    [Fact]
    public void BibleProposalV1_DeserialisesTheSnakeCaseAnswer()
    {
        const string Json = """
            {"ops": [
              {"kind": "add_summary", "summary": "Một người trở về căn nhà cũ.", "name": "Tập một", "description": null, "text": null, "reason": "tập này chốt lại mạch truyện"},
              {"kind": "add_character", "summary": null, "name": "Bà Tư", "description": "Người giữ căn nhà cũ.", "text": null, "reason": null}
            ]}
            """;

        var answer = JsonSerializer.Deserialize<BibleProposalV1>(Json, Options);

        answer.ShouldNotBeNull();
        var ops = answer!.Ops;
        ops.Count.ShouldBe(2);
        ops[0].Kind.ShouldBe("add_summary");
        ops[0].Summary.ShouldBe("Một người trở về căn nhà cũ.");
        ops[0].Name.ShouldBe("Tập một");
        ops[0].Description.ShouldBeNull();
        ops[0].Text.ShouldBeNull();
        ops[0].Reason.ShouldBe("tập này chốt lại mạch truyện");
        ops[1].Kind.ShouldBe("add_character");
        ops[1].Name.ShouldBe("Bà Tư");
        ops[1].Reason.ShouldBeNull();
    }

    [Fact]
    public void ToCandidates_MapsEveryOpInOrderWithoutChangingAField()
    {
        var answer = new BibleProposalV1(
        [
            new BibleProposalOpV1("add_place", null, "Ngôi nhà cuối ngõ", "Nhà gỗ hai tầng, cửa luôn mở.", null, "cảnh này quay lại"),
            new BibleProposalOpV1("add_rule", null, null, null, "Không nói tên người chết.", null),
        ]);

        var candidates = answer.ToCandidates();

        candidates.Count.ShouldBe(2);
        candidates[0].Kind.ShouldBe("add_place");
        candidates[0].Name.ShouldBe("Ngôi nhà cuối ngõ");
        candidates[0].Description.ShouldBe("Nhà gỗ hai tầng, cửa luôn mở.");
        candidates[0].Reason.ShouldBe("cảnh này quay lại");
        candidates[1].Text.ShouldBe("Không nói tên người chết.");
        candidates[1].Summary.ShouldBeNull();
    }

    [Fact]
    public void BibleProposalV1_EmptyOps_AreNoCandidates()
    {
        var answer = JsonSerializer.Deserialize<BibleProposalV1>("""{"ops": []}""", Options);

        answer.ShouldNotBeNull();
        answer!.ToCandidates().ShouldBeEmpty();
    }

    [Fact]
    public void ToCandidates_KindOutsideTheSchemaEnum_IsPassedThroughAndDroppedByTheValidator()
    {
        // The schema's enum restricts the kind; the DTO does not, on purpose: a kind the closed list does not know
        // must cost one op, never turn the whole answer into an invalid-output failure (plan §7.1 step 5).
        var answer = JsonSerializer.Deserialize<BibleProposalV1>(
            """{"ops": [{"kind": "set_narrator", "summary": null, "name": "Người kể chuyện", "description": null, "text": null, "reason": null}]}""",
            Options);

        answer.ShouldNotBeNull();
        var candidates = answer!.ToCandidates();
        candidates.ShouldHaveSingleItem().Kind.ShouldBe("set_narrator");

        var result = BibleProposalValidator.Validate(candidates, EmptyBible(), new BibleProposalCaps());

        result.Ops.ShouldBeEmpty();
        result.Drops.ShouldHaveSingleItem().ShouldBe(new BibleOpDrop(BibleProposalValidator.UnknownKindName, BibleProposalValidator.UnknownKind));
    }

    [Fact]
    public void BibleInjectionFixtures_AreShippedForTheBibleStep()
    {
        var ids = InjectionFixture.Ids("bible");

        ids.ShouldBe(BibleFixtureIds);
        InjectionFixture.Ids().Intersect(ids, StringComparer.Ordinal).ShouldBeEmpty("a bible fixture must not be read as a writer fixture");

        var fixtures = ids.Select(InjectionFixture.Load).ToList();
        fixtures.Select(fixture => fixture.Canary).Distinct().Count().ShouldBe(ids.Count);
        fixtures.ShouldAllBe(fixture => fixture.Step == "bible");
        fixtures.ShouldAllBe(fixture => fixture.Expect.SchemaValid, "the answer to a hostile payload is still a valid proposal");
        fixtures.ShouldAllBe(fixture => fixture.Expect.NoPayloadEcho);
        fixtures.ShouldAllBe(fixture => fixture.Value.Contains(fixture.Canary, StringComparison.Ordinal));

        // The payload travels in the untrusted material of the proposal's data object, never in a control key.
        fixtures.ShouldAllBe(fixture => fixture.Field.StartsWith("bible.", StringComparison.Ordinal));
    }

    private static SeriesBibleContent EmptyBible() =>
        new(
            SeriesBibleContent.V1,
            new BibleNarrator("Người kể chuyện", "Kể chuyện đêm khuya cho người lớn.", string.Empty),
            [],
            [],
            [],
            []);
}
