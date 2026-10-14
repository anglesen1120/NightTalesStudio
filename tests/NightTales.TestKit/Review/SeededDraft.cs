using System.Text.Json;
using System.Text.Json.Serialization;
using NightTales.Application.Abstractions.Research;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Content;

namespace NightTales.TestKit.Review;

/// <summary>
/// One frozen seeded-defect draft of <c>prompts/fixtures/seeded-defects/&lt;id&gt;.json</c> (02.6.1-T2, 02 README
/// T27): a synthetic <c>story.v1</c> draft, the objective defects planted in it (empty for a clean draft) and the
/// true-crime facts/bible some drafts need. The bytes on disk are the fixture contract; this record is the typed
/// view the scorer and the acceptance harness read, and it never carries a real person, a real crime or real-place
/// claims (the set is synthetic by construction).
/// </summary>
/// <remarks>
/// The JSON is snake_case and the enums are their wire values (<c>vi</c>/<c>en</c>, <c>ghost</c>/<c>spiritual</c>/
/// <c>true_crime</c>, <c>standard</c>), so a fixture is written exactly as the D3/D22 contracts spell them. The file
/// name without its extension is the id; the loader asserts the field matches, so a renamed file never silently
/// becomes a second draft.
/// </remarks>
/// <param name="Id">The fixture id; also the file name without extension.</param>
/// <param name="Language">The content language of the draft, <c>vi</c> or <c>en</c>.</param>
/// <param name="Topic">The series topic of the draft: <c>ghost</c>, <c>spiritual</c> or <c>true_crime</c>.</param>
/// <param name="Tier">The length tier the draft was written for; <c>standard</c> unless a <c>length_out_of_range</c> defect needs another band.</param>
/// <param name="Clean"><see langword="true"/> for a draft that must review with no blocking issue; <see cref="Defects"/> is then empty.</param>
/// <param name="Brief">The synthetic one-paragraph premise; context for a human reader, never sent to the model.</param>
/// <param name="Draft">The <c>story.v1</c> draft under review.</param>
/// <param name="Facts">The approved facts; <see langword="null"/> when the fixture omits the field. True-crime drafts carry them.</param>
/// <param name="Bible">The series bible; <see langword="null"/> for a draft without one (only <c>bible_contradiction</c> fixtures need it).</param>
/// <param name="Defects">The planted objective defects; <see langword="null"/> when the field is absent, empty for a clean draft.</param>
public sealed record SeededDraft(
    string Id,
    Language Language,
    Topic Topic,
    LengthTier Tier,
    bool Clean,
    string Brief,
    StoryContentV1 Draft,
    IReadOnlyList<FactForWriter>? Facts = null,
    BibleSnapshot? Bible = null,
    IReadOnlyList<SeededDefect>? Defects = null)
{
    /// <summary>The planted defects, never <see langword="null"/>: an absent <c>defects</c> field reads as empty.</summary>
    public IReadOnlyList<SeededDefect> SeedDefects => Defects ?? [];

    /// <summary>The approved facts, never <see langword="null"/>: an absent <c>facts</c> field reads as empty (fiction, or true crime with no sheet).</summary>
    public IReadOnlyList<FactForWriter> ApprovedFacts => Facts ?? [];

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
    };

    /// <summary>The directory the frozen set lives in: <c>&lt;repo root&gt;/prompts/fixtures/seeded-defects</c>.</summary>
    /// <returns>The absolute fixture directory.</returns>
    public static string FixtureDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NightTales.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root (NightTales.sln) above the test output.");

        return Path.Combine(root, "prompts", "fixtures", "seeded-defects");
    }

    /// <summary>The fixture ids shipped in the frozen set, ordered by name.</summary>
    /// <returns>The file names without extension.</returns>
    public static IReadOnlyList<string> Ids() =>
        [.. Directory.EnumerateFiles(FixtureDirectory(), "*.json").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal)!];

    /// <summary>Loads one fixture by id.</summary>
    /// <param name="id">The fixture id; also the file name without extension.</param>
    /// <returns>The parsed fixture.</returns>
    /// <exception cref="InvalidOperationException">The file is empty, or its <c>id</c> does not match the file name.</exception>
    public static SeededDraft Load(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var draft = JsonSerializer.Deserialize<SeededDraft>(File.ReadAllText(Path.Combine(FixtureDirectory(), id + ".json")), Options)
            ?? throw new InvalidOperationException($"Seeded-defect fixture '{id}' is empty.");

        if (!string.Equals(draft.Id, id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Seeded-defect fixture '{id}' declares id '{draft.Id}'; the file name and the field must match.");
        }

        return draft;
    }

    /// <summary>Loads every fixture of the frozen set, ordered by id.</summary>
    /// <returns>The parsed fixtures.</returns>
    public static IReadOnlyList<SeededDraft> LoadAll() => [.. Ids().Select(Load)];

    /// <summary>Maps a fixture to the request pieces a review call needs (02.6.1-T1 <c>StoryReviewRequest</c>): the draft, its language/tier/topic, the facts and the bible.</summary>
    /// <returns>The mapped pieces; the caller supplies the run, the resolved <c>LengthTarget</c>, the ear-rule questions and the preset key.</returns>
    public SeededDraftReviewInput ToReviewInput() =>
        new(Draft, Language, Tier, Topic, ApprovedFacts, Bible);
}

/// <summary>
/// One planted objective defect of a seeded draft (02.6.1-T2): the wire type name of an objective
/// <see cref="NightTales.Domain.Stories.Review.ReviewIssueType"/>, the D22 segment address the defect sits at, and a
/// short note for a human reader. The scorer's detection rule matches <see cref="Type"/> <b>and</b>
/// <see cref="Segment"/>, so <see cref="Segment"/> must be the address <c>prompts/review/v1.md</c> would use
/// (<c>title</c>, <c>hook</c>, <c>ending</c>, <c>meta</c> or <c>p:N</c>; the paragraph index is zero-based, so
/// <c>p:0</c> is the first paragraph).
/// </summary>
/// <param name="Type">The snake_case wire name of an objective issue type.</param>
/// <param name="Segment">The D22 segment address the defect sits at.</param>
/// <param name="Note">Why it is a defect; never model-facing.</param>
public sealed record SeededDefect(string Type, string Segment, string Note);

/// <summary>The draft-side pieces of a review request, mapped from a <see cref="SeededDraft"/> (02.6.1-T2).</summary>
/// <param name="Draft">The <c>story.v1</c> draft to review.</param>
/// <param name="Language">The content language.</param>
/// <param name="Tier">The length tier.</param>
/// <param name="Topic">The series topic.</param>
/// <param name="Facts">The approved facts; empty for fiction or a true-crime draft without a sheet.</param>
/// <param name="Bible">The series bible, or <see langword="null"/>.</param>
public sealed record SeededDraftReviewInput(
    StoryContentV1 Draft,
    Language Language,
    LengthTier Tier,
    Topic Topic,
    IReadOnlyList<FactForWriter> Facts,
    BibleSnapshot? Bible);
