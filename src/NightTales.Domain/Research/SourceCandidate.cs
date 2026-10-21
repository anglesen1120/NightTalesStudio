using ErrorOr;
using NightTales.Domain.Common;

namespace NightTales.Domain.Research;

/// <summary>
/// One source a find-sources run proposed for an episode (02.7.2 plan §3, table <c>source_candidates</c>): a
/// citation URL that passed the D11 URL rules and whose registrable domain was on the allow-list at proposal
/// time. It is a child of the episode aggregate and is only ever reached through an episode id.
/// </summary>
/// <remarks>
/// <para>
/// A candidate is never a source: the run only proposes, and adding one goes through the normal 02.5.1 source
/// path (<see cref="MarkAdded"/> records the <see cref="StorySource"/> id that path created). Nothing is added
/// automatically, and the URL is proposed at most once per episode — the unique
/// <c>(episode_id, normalized_url)</c> index, which a later run relies on to skip an already-known URL.
/// </para>
/// <para>
/// <see cref="Status"/> is stored as snake_case text with a CHECK (09 README D8) and moves only
/// <see cref="SourceCandidateStatus.Proposed"/> → <see cref="SourceCandidateStatus.Added"/> or
/// <see cref="SourceCandidateStatus.Dismissed"/>. A dismissed candidate is a no-op to dismiss again, an added one
/// cannot be dismissed, and an <see cref="SourceCandidateStatus.Added"/> row always carries
/// <see cref="SourceId"/> (the <c>ck_source_candidates_added</c> invariant).
/// </para>
/// </remarks>
public sealed class SourceCandidate : Entity, IUserOwned, IAuditable
{
    /// <summary>The longest citation URL, as cited and after normalisation (02 D11, plan §3).</summary>
    public const int UrlMaxChars = SourceUrl.MaxLength;

    /// <summary>The longest registrable domain (a DNS name limit, as <see cref="StorySource"/>).</summary>
    public const int DomainMaxChars = 253;

    /// <summary>The longest citation title (plan §3); the title is plain text, never rendered as HTML.</summary>
    public const int TitleMaxChars = 300;

    /// <summary>The first rank of a run's citation order (plan §3).</summary>
    public const short MinRank = 1;

    /// <summary>The last rank: one run proposes at most ten citations (plan §3).</summary>
    public const short MaxRank = 10;

    /// <summary>
    /// The catalogued code of adding or dismissing a candidate that is no longer proposed (409). It equals
    /// <c>ErrorCodes.SourceCandidateNotProposed</c>, which the Domain may not reference; the Domain unit test
    /// <c>SourceCandidate_NotProposedCode_MatchesTheErrorCatalogue</c> keeps the two equal.
    /// </summary>
    public const string NotProposedCode = "source_candidate_not_proposed";

    private SourceCandidate()
    {
    }

    /// <inheritdoc />
    public Guid UserId { get; private set; }

    /// <summary>The episode the candidate belongs to.</summary>
    public Guid EpisodeId { get; private set; }

    /// <summary>The <c>find_sources</c> run that proposed it.</summary>
    public Guid GenerationRunId { get; private set; }

    /// <summary>The citation URL as cited (also the URL later handed to the add-source path).</summary>
    public string Url { get; private set; } = "";

    /// <summary>The URL after the shared 02.5.1 D11 normaliser; what the per-episode uniqueness rule compares.</summary>
    public string NormalizedUrl { get; private set; } = "";

    /// <summary>The registrable domain of <see cref="NormalizedUrl"/> (PSL, 02.5.1-T1).</summary>
    public string RegistrableDomain { get; private set; } = "";

    /// <summary>The citation title, at most <see cref="TitleMaxChars"/> characters; <see langword="null"/> when absent.</summary>
    public string? Title { get; private set; }

    /// <summary>The allow-list entry's display name at proposal time.</summary>
    public string SiteName { get; private set; } = "";

    /// <summary>The citation order of the run, <see cref="MinRank"/> to <see cref="MaxRank"/>.</summary>
    public short Rank { get; private set; }

    /// <summary>The lifecycle status; see the class remarks for the only allowed transitions.</summary>
    public SourceCandidateStatus Status { get; private set; }

    /// <summary>The source an added candidate created; <see langword="null"/> until <see cref="MarkAdded"/>.</summary>
    public Guid? SourceId { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Creates a <see cref="SourceCandidateStatus.Proposed"/> candidate from one citation of a find-sources run.
    /// The caller has already applied the D11 URL rules and the allow-list (plan §3 invariants), so this factory
    /// only guards what a stored row must satisfy.
    /// </summary>
    /// <param name="userId">The owner of the episode.</param>
    /// <param name="episodeId">The episode.</param>
    /// <param name="generationRunId">The <c>find_sources</c> run that proposed the citation.</param>
    /// <param name="url">The citation URL as cited; blank is rejected.</param>
    /// <param name="normalizedUrl">The URL after the D11 normaliser; blank is rejected.</param>
    /// <param name="registrableDomain">The registrable domain of the normalised URL; blank is rejected.</param>
    /// <param name="title">The citation title, or <see langword="null"/>; trimmed, empty becomes <see langword="null"/>.</param>
    /// <param name="siteName">The allow-list entry's display name; blank is rejected.</param>
    /// <param name="rank">The citation order, <see cref="MinRank"/> to <see cref="MaxRank"/>.</param>
    /// <returns>The new candidate, in <see cref="SourceCandidateStatus.Proposed"/> with no source.</returns>
    /// <exception cref="ArgumentException">An owner, episode, run, URL, normalised URL, domain or site name is empty, or a string exceeds its cap.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rank"/> is outside 1…10.</exception>
    public static SourceCandidate Create(
        Guid userId,
        Guid episodeId,
        Guid generationRunId,
        string url,
        string normalizedUrl,
        string registrableDomain,
        string? title,
        string siteName,
        short rank)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A source candidate needs an owner.", nameof(userId));
        }

        if (episodeId == Guid.Empty)
        {
            throw new ArgumentException("A source candidate needs an episode.", nameof(episodeId));
        }

        if (generationRunId == Guid.Empty)
        {
            throw new ArgumentException("A source candidate needs the run that proposed it.", nameof(generationRunId));
        }

        var citedUrl = Required(url, UrlMaxChars, nameof(url));
        var normalized = Required(normalizedUrl, UrlMaxChars, nameof(normalizedUrl));
        var domain = Required(registrableDomain, DomainMaxChars, nameof(registrableDomain));
        var site = Required(siteName, null, nameof(siteName));

        var citationTitle = title?.Trim();
        if (citationTitle is { Length: 0 })
        {
            citationTitle = null;
        }

        if (citationTitle is { Length: > TitleMaxChars })
        {
            throw new ArgumentException(
                $"A citation title is at most {TitleMaxChars} characters.",
                nameof(title));
        }

        if (rank is < MinRank or > MaxRank)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rank),
                rank,
                $"A citation rank is {MinRank} to {MaxRank}.");
        }

        return new SourceCandidate
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            EpisodeId = episodeId,
            GenerationRunId = generationRunId,
            Url = citedUrl,
            NormalizedUrl = normalized,
            RegistrableDomain = domain,
            Title = citationTitle,
            SiteName = site,
            Rank = rank,
            Status = SourceCandidateStatus.Proposed,
        };
    }

    /// <summary>
    /// Records that the candidate was added to the episode through the normal source path:
    /// <see cref="SourceCandidateStatus.Proposed"/> → <see cref="SourceCandidateStatus.Added"/>.
    /// </summary>
    /// <param name="sourceId">The <see cref="StorySource"/> the add path created.</param>
    /// <returns>Success, or <see cref="NotProposedCode"/> when the candidate is not proposed.</returns>
    /// <exception cref="ArgumentException"><paramref name="sourceId"/> is empty.</exception>
    public ErrorOr<Success> MarkAdded(Guid sourceId)
    {
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("An added candidate needs its source id.", nameof(sourceId));
        }

        if (Status != SourceCandidateStatus.Proposed)
        {
            return NotProposed();
        }

        SourceId = sourceId;
        Status = SourceCandidateStatus.Added;

        return Result.Success;
    }

    /// <summary>
    /// Dismisses the candidate: <see cref="SourceCandidateStatus.Proposed"/> → <see cref="SourceCandidateStatus.Dismissed"/>.
    /// Dismissing twice is a no-op, so a retried request or a double click answers 204 both times.
    /// </summary>
    /// <returns>Success, or <see cref="NotProposedCode"/> when the candidate was already added.</returns>
    public ErrorOr<Success> Dismiss()
    {
        if (Status == SourceCandidateStatus.Dismissed)
        {
            return Result.Success;
        }

        if (Status != SourceCandidateStatus.Proposed)
        {
            return NotProposed();
        }

        Status = SourceCandidateStatus.Dismissed;

        return Result.Success;
    }

    private static Error NotProposed() => Error.Conflict(
        NotProposedCode,
        "Only a proposed source candidate can be added or dismissed.");

    /// <summary>A required trimmed string, at most <paramref name="maxChars"/> characters when one is fixed.</summary>
    private static string Required(string? value, int? maxChars, string parameterName)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ArgumentException($"'{parameterName}' is required.", parameterName);
        }

        if (maxChars is { } max && trimmed.Length > max)
        {
            throw new ArgumentException($"'{parameterName}' is at most {max} characters.", parameterName);
        }

        return trimmed;
    }
}
