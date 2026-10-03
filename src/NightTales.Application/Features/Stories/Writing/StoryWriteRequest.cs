using NightTales.Application.Abstractions.Research;
using NightTales.Application.Abstractions.Text;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Length;
using NightTales.Domain.Stories.Structure;

namespace NightTales.Application.Features.Stories.Writing;

/// <summary>Everything one writer call needs (02 README D21).</summary>
/// <remarks>
/// 02.2.3-T2 declares the request so the prompt assembler compiles against the final shape; 02.2.1-T2
/// owns <c>IStoryWriter</c> and 02.2.2-T3 the factory that builds it. <see cref="ViolatedRules"/> is the
/// invalid-output handshake of the 02.2 plan §4: rule ids only, set on the retry and never the invalid text.
/// </remarks>
/// <param name="Run">The run context; <c>Attempt</c> decides whether a corrective note is sent.</param>
/// <param name="Series">The series the episode belongs to.</param>
/// <param name="Episode">The episode being written.</param>
/// <param name="Length">The resolved length contract of the tier × language cell.</param>
/// <param name="Bible">The series bible, or <see langword="null"/> for a series without one.</param>
/// <param name="Facts">The approved facts, or <see langword="null"/> when there is no approved sheet.</param>
/// <param name="AvoidPlots">The plots the writer must not repeat; empty when none.</param>
/// <param name="ViolatedRules">The validator rule ids of the first attempt; <see langword="null"/> on attempt 1.</param>
public sealed record StoryWriteRequest(
    RunContext Run,
    SeriesSnapshot Series,
    EpisodeSnapshot Episode,
    LengthTarget Length,
    BibleSnapshot? Bible,
    IReadOnlyList<FactForWriter>? Facts,
    IReadOnlyList<string> AvoidPlots,
    IReadOnlyList<string>? ViolatedRules);

/// <summary>The series facts a writer call reads.</summary>
/// <param name="SeriesId">The series' id.</param>
/// <param name="Topic">The narrative topic.</param>
/// <param name="Language">The content language.</param>
public sealed record SeriesSnapshot(Guid SeriesId, Topic Topic, Language Language);

/// <summary>The episode facts a writer call reads.</summary>
/// <param name="EpisodeId">The episode's id.</param>
/// <param name="Brief">The author's idea or brief; untrusted text, sent only inside the data object.</param>
/// <param name="LengthTier">The episode's length tier.</param>
/// <param name="Structure">The variety guard's plan, or <see langword="null"/> when it does not steer this episode.</param>
public sealed record EpisodeSnapshot(Guid EpisodeId, string Brief, LengthTier LengthTier, StructurePlan? Structure);
