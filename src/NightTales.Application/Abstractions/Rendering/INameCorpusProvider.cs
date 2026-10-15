using NightTales.Domain.Stories.Keywords;

namespace NightTales.Application.Abstractions.Rendering;

/// <summary>
/// The name corpus of one episode (03 README D11, 03.2.2-T1): the real-person names the episode's own
/// research carries — the titles of its fetched <c>url</c> sources and the statements of its approved
/// fact sheet — that the render keyword linter flags keywords against.
/// </summary>
/// <remarks>
/// <para>
/// Fiction and unknown episodes have no corpus (<see cref="NameCorpus.Empty"/>): the episode's series
/// topic decides, and only <c>true_crime</c> has a real-person corpus (D11 [DECISION]). The read is the
/// ambient tenant-filtered one, so another user's episode is never visible and yields
/// <see cref="NameCorpus.Empty"/>.
/// </para>
/// <para>
/// The caller MUST have loaded and authorised the episode in the same scope before calling: the user
/// context is set and another user's episode is a 404 at the route, not here. This read is tenant
/// filtered, so it cannot tell "no names" from "not visible":
/// <see cref="NameCorpus.Empty"/> is <em>not</em> evidence that the episode is clean. It is also
/// returned when the linter's name rule is off, when the episode is invisible or unknown, when its
/// series topic is not <c>true_crime</c>, or when it carries no research text.
/// </para>
/// </remarks>
public interface INameCorpusProvider
{
    /// <summary>Loads the corpus the lint of <paramref name="episodeId"/>'s keywords runs against.</summary>
    /// <remarks>
    /// The caller MUST have loaded and authorised <paramref name="episodeId"/> in the same scope before
    /// calling. A name rule that is off, an invisible or unknown episode, a series topic that is not
    /// <c>true_crime</c> and an episode with no research text all yield
    /// <see cref="NameCorpus.Empty"/>, which is therefore not evidence that the episode is clean.
    /// </remarks>
    /// <param name="episodeId">The episode whose sources and approved fact sheet build the corpus.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>
    /// The corpus, or <see cref="NameCorpus.Empty"/> when the linter's name rule is off, the episode is
    /// not visible, its series topic is not true crime, or it carries no title and no approved fact.
    /// </returns>
    ValueTask<NameCorpus> LoadAsync(Guid episodeId, CancellationToken ct);
}
