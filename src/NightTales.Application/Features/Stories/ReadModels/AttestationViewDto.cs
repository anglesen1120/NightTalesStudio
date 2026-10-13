using NightTales.Application.Features.Stories.Approval;

namespace NightTales.Application.Features.Stories.ReadModels;

/// <summary>
/// The true-crime attestation state of the current story version (02.3.3-T1, CR-7 extension): what the
/// approval dialog renders and what a stored tick is compared to.
/// </summary>
/// <remarks>
/// <see cref="Required"/> is the pinned-data rule the approval gate applies: a series whose topic is
/// true crime, or a version that carries a fact sheet id, is governed whatever the series topic says
/// now, so a fiction → true-crime topic patch cannot make an approved story look unattested. For an
/// episode that is not governed the whole member is <see langword="null"/> instead of a false flag.
/// </remarks>
/// <param name="Required">Whether this episode must carry an accepted tick to be approved.</param>
/// <param name="TextVersion">The configured <c>Story:TrueCrime:AttestationVersion</c> the tick must name.</param>
/// <param name="Text">
/// The clause text of the episode's language, or <see langword="null"/> when the release does not ship
/// the configured version: the version is the single accepted value, so a dialog that showed other
/// wording would invite a tick the gate then rejects.
/// </param>
/// <param name="AcceptedAt">
/// When the current version was attested, or <see langword="null"/> when it was not or no longer is: a
/// tick counts only for the version that is approved right now — re-opening clears that pointer — and
/// only when it names the configured text version. Read from the <c>episode.true_crime_attested</c>
/// audit row of this version, never from a story column.
/// </param>
public sealed record AttestationViewDto(
    bool Required,
    string TextVersion,
    string? Text,
    DateTimeOffset? AcceptedAt);
