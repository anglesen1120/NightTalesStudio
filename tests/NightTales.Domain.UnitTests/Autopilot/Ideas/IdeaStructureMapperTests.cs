using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Common;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Structure;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Autopilot.Ideas;

/// <summary>
/// The idea-tag vocabulary mapping (02.8.4-T1, IDEA-S52-1): the shared D3 mapper applied to one candidate's three
/// vocabulary-checked structure values, with the shared vocabulary document as its only source.
/// </summary>
/// <remarks>
/// The fixture is the shape of <c>prompts/shared/structure-vocab.v1.json</c> shrunk to one topic and one dimension
/// of each kind, because what is pinned here is the reader and the mapping rule, not the shipped vocabulary (the
/// writer's own tests own that file).
/// </remarks>
public sealed class IdeaStructureMapperTests
{
    private const string Vocabulary = """
        {"version":1,"topics":{"ghost":{
          "sub_genre":{"vi":["truyện ma tâm linh"],"en":["quiet haunting"]},
          "opening":{"vi":["một cuộc điện thoại lúc nửa đêm"],"en":["a call at midnight"]},
          "twist_type":{"vi":["người kể là nguyên nhân"],"en":["the narrator caused it"]}}}}
        """;

    /// <summary>The rules of the topic and language the <c>Vocabulary</c> fixture below describes.</summary>
    private static StoryRules Rules { get; } = new()
    {
        Language = Language.Vi,
        StructureVocab = IdeaStructureMapper.Vocabulary(Vocabulary, "ghost", Language.Vi),
    };

    /// <summary>A vocabulary term is stored as it stands and raises no issue.</summary>
    [Fact]
    public void Map_KnownValues_AreStoredAsTheyStandAndAreSilent()
    {
        var mapping = IdeaStructureMapper.Map(Candidate("truyện ma tâm linh", "một cuộc điện thoại lúc nửa đêm", "người kể là nguyên nhân"), Rules);

        mapping.Candidate.Structure.SubGenre.ShouldBe("truyện ma tâm linh");
        mapping.Candidate.Structure.Opening.ShouldBe("một cuộc điện thoại lúc nửa đêm");
        mapping.Candidate.Structure.TwistType.ShouldBe("người kể là nguyên nhân");
        mapping.IsBlocked.ShouldBeFalse();
        mapping.Issues.ShouldBeEmpty("a value the vocabulary carries is not reported");
    }

    /// <summary>
    /// A value outside the vocabulary is stored as <c>other:&lt;text&gt;</c> — the prompt's promise — and the
    /// shared mapper's advisory issue is raised, which never blocks the candidate.
    /// </summary>
    [Fact]
    public void Map_UnknownValues_AreStoredAsOtherAndAreAdvisory()
    {
        var mapping = IdeaStructureMapper.Map(Candidate("truyện ma", "một cuộc điện thoại lúc nửa đêm", "người kể là nguyên nhân"), Rules);

        mapping.Candidate.Structure.SubGenre.ShouldBe("other:truyện ma");
        mapping.Candidate.Structure.Opening.ShouldBe("một cuộc điện thoại lúc nửa đêm");
        mapping.IsBlocked.ShouldBeFalse("an unmapped value is advisory only: the guard must never fail a run on vocabulary");
        mapping.Issues.ShouldHaveSingleItem().Severity.ShouldBe(IssueSeverity.Advisory);
    }

    /// <summary>A model-supplied <c>other:</c> is stripped and mapped once, so the prefix is never doubled.</summary>
    [Fact]
    public void Map_AlreadyOtherPrefixedValue_IsNormalisedOnce()
    {
        var mapping = IdeaStructureMapper.Map(Candidate("other:truyện ma tâm linh", "other:lời nguyền có thật", "người kể là nguyên nhân"), Rules);

        mapping.Candidate.Structure.SubGenre.ShouldBe("truyện ma tâm linh", "the term it names is a vocabulary value");
        mapping.Candidate.Structure.Opening.ShouldBe("other:lời nguyền có thật", "the term it names is not an opening of the vocabulary, so the prefix is re-added once");
        mapping.Candidate.Structure.Opening.ShouldNotContain("other:other:");
        mapping.Candidate.Structure.TwistType.ShouldBe("người kể là nguyên nhân");
    }

    /// <summary>
    /// Only the three vocabulary dimensions are mapped: the setting stays the model's free text, and the narrator
    /// mode and the beats are untouched.
    /// </summary>
    [Fact]
    public void Map_FreeTextAndBeats_AreUntouched()
    {
        var candidate = Candidate("truyện ma", "một cuộc điện thoại lúc nửa đêm", "người kể là nguyên nhân") with
        {
            Setting = "một ngôi nhà cũ bên sông",
        };
        var beats = candidate.Beats;

        var mapping = IdeaStructureMapper.Map(candidate, Rules);

        mapping.Candidate.Setting.ShouldBe("một ngôi nhà cũ bên sông");
        mapping.Candidate.Structure.Setting.ShouldBe("ngôi nhà cũ");
        mapping.Candidate.Structure.NarratorMode.ShouldBe("first_person");
        mapping.Candidate.Beats.ShouldBe(beats);
    }

    /// <summary>
    /// A blank value stays blank rather than becoming the value <c>other:</c>: a dimension the model left empty
    /// says nothing, which is not the same as a value the guard could count.
    /// </summary>
    [Fact]
    public void Map_BlankValue_StaysBlank()
    {
        var mapping = IdeaStructureMapper.Map(Candidate("   ", "một cuộc điện thoại lúc nửa đêm", "người kể là nguyên nhân"), Rules);

        mapping.Candidate.Structure.SubGenre.ShouldBe("   ");
        mapping.Issues.ShouldBeEmpty();
    }

    /// <summary>
    /// A value over the free-string limit is a blocking issue, which is the only reason the planner drops a
    /// candidate here (a safety net: the idea validator refuses the same shapes first).
    /// </summary>
    [Fact]
    public void Map_OverLongValue_IsBlocking()
    {
        var mapping = IdeaStructureMapper.Map(Candidate(new string('x', 41), "một cuộc điện thoại lúc nửa đêm", "người kể là nguyên nhân"), Rules);

        mapping.IsBlocked.ShouldBeTrue();
        mapping.Candidate.Structure.SubGenre.ShouldBe("other:" + new string('x', 41), "the value is still mapped; the caller drops the candidate");
    }

    /// <summary>
    /// A damaged, absent or unknown vocabulary is not a reason to fail a run: nothing is known, so every checked
    /// value is stored as <c>other:&lt;text&gt;</c> and no exception is raised.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("""{"topics":{"true_crime":{"sub_genre":{"vi":["a case"]}}}}""")]
    public void Map_UnreadableOrUnknownVocabulary_MapsEverythingToOther(string vocabulary)
    {
        var rules = new StoryRules
        {
            Language = Language.Vi,
            StructureVocab = IdeaStructureMapper.Vocabulary(vocabulary, "ghost", Language.Vi),
        };

        var mapping = IdeaStructureMapper.Map(Candidate("truyện ma tâm linh", "một cuộc điện thoại lúc nửa đêm", "người kể là nguyên nhân"), rules);

        mapping.IsBlocked.ShouldBeFalse();
        mapping.Candidate.Structure.SubGenre.ShouldBe("other:truyện ma tâm linh");
        mapping.Candidate.Structure.Opening.ShouldBe("other:một cuộc điện thoại lúc nửa đêm");
        mapping.Candidate.Structure.TwistType.ShouldBe("other:người kể là nguyên nhân");
    }

    /// <summary>
    /// The vocabulary reader takes the listed terms of one topic and one language only: the other language's terms
    /// of the same dimension are not part of it, and the setting lists are never read (setting is free text).
    /// </summary>
    [Fact]
    public void Vocabulary_TakesTheTopicAndTheLanguage()
    {
        var vi = IdeaStructureMapper.Vocabulary(Vocabulary, "ghost", Language.Vi);
        var en = IdeaStructureMapper.Vocabulary(Vocabulary, "ghost", Language.En);

        vi.ShouldBe(["truyện ma tâm linh", "một cuộc điện thoại lúc nửa đêm", "người kể là nguyên nhân"], ignoreOrder: true);
        en.ShouldBe(["quiet haunting", "a call at midnight", "the narrator caused it"], ignoreOrder: true);
        IdeaStructureMapper.Vocabulary(Vocabulary, "true_crime", Language.Vi).ShouldBeEmpty();
        IdeaStructureMapper.Vocabulary(Vocabulary, null, Language.Vi).ShouldBeEmpty();
    }

    /// <summary>One candidate of the idea validator's shape, with the three values the test drives.</summary>
    /// <param name="subGenre">The candidate's <c>sub_genre</c>.</param>
    /// <param name="opening">The candidate's <c>opening</c>.</param>
    /// <param name="twistType">The candidate's <c>twist_type</c>.</param>
    /// <returns>The candidate.</returns>
    private static IdeaCandidate Candidate(string subGenre, string opening, string twistType) =>
        new(
            "Một cái tên",
            "Một lời giới thiệu ngắn về chuyện sắp kể.",
            "Đêm ấy có tiếng gõ cửa lúc nửa đêm.",
            "một ngôi nhà cũ bên sông",
            new StoryStructureTags(subGenre, "ngôi nhà cũ", opening, twistType, "first_person", ["setup", "clue", "twist"]),
            ["setup", "clue", "twist"]);
}
