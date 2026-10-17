using ErrorOr;
using NightTales.Domain.Common;
using NightTales.Domain.Stories.Structure;

namespace NightTales.Domain.Autopilot.Ideas;

/// <summary>
/// One idea the autopilot proposed for a series (02.8.1 plan §3.1, <c>episode_ideas</c>): the four text
/// fields, the D3 structure block and the outline of the episode the idea would become, plus the pick's
/// measurements.
/// </summary>
/// <remarks>
/// <para>
/// A row is created <see cref="IdeaStatus.Proposed"/> by the run that paid for the model call
/// (<see cref="CreateProposed"/>), and the state machine is the plan's: 02.8.1-T2 marks the winner
/// <see cref="MarkPicked"/> and the rest <see cref="MarkRejected"/>, and 02.8.3-T1 marks the picked idea
/// <see cref="MarkUsed"/> once its episode exists. <see cref="MarkUsed"/> is the only transition an HTTP
/// command can drive, so it is the only one that answers an <see cref="Error"/> — a second call for the same
/// idea is a conflict (<c>idea_invalid_state</c>), which is also what the partial unique index on
/// <c>episode_id</c> enforces at the table.
/// </para>
/// <para>
/// The other transitions throw <see cref="InvalidOperationException"/> on a contradiction instead of
/// returning an error, the way <c>GenerationRun</c> does: they are called by the run's own pipeline, which
/// reads the status first, so a wrong one is a programming error rather than a user-facing answer. Repeating
/// a transition that already reached its target is a no-op, because the at-least-once outbox (09.2.4) may
/// deliver the same step twice.
/// </para>
/// <para>
/// The text fields are model output and are therefore never logged (D19); the row is deleted with its series.
/// </para>
/// </remarks>
public sealed class EpisodeIdea : Entity, IUserOwned, IAuditable
{
    /// <summary>
    /// The longest <c>rejected_reason</c> (the column is <c>varchar(40)</c>), the bound every value of
    /// <see cref="IdeaRejectionReason"/> satisfies.
    /// </summary>
    public const int RejectedReasonMaxChars = IdeaRejectionReason.MaxLength;

    /// <summary>
    /// The code of marking an idea that is not picked as used (409). The Domain spells the literal because it
    /// may not reference <c>ErrorCodes</c> (D1: ErrorOr is its only package); the
    /// <c>ErrorCodes.IdeaInvalidState</c> constant and its 409 catalogue row are deferred to 02.8.1-T2, and the
    /// join test that keeps the two equal lands with them. Until then
    /// <c>EpisodeIdea_InvalidStateCode_IsThePlannedCatalogueCode</c> pins this literal to the plan's code.
    /// </summary>
    public const string InvalidStateCode = "idea_invalid_state";

    private EpisodeIdea()
    {
    }

    /// <inheritdoc />
    public Guid UserId { get; private set; }

    /// <summary>The series the idea belongs to.</summary>
    public Guid SeriesId { get; private set; }

    /// <summary>The <c>ideas</c> run that paid for the model call that produced it.</summary>
    public Guid GenerationRunId { get; private set; }

    /// <summary>The episode title as proposed.</summary>
    public string Title { get; private set; } = "";

    /// <summary>The one-line premise as proposed.</summary>
    public string Logline { get; private set; } = "";

    /// <summary>The opening hook as proposed.</summary>
    public string Hook { get; private set; } = "";

    /// <summary>The setting in words as proposed; the setting tag lives in <see cref="Structure"/>.</summary>
    public string Setting { get; private set; } = "";

    /// <summary>The D3 structure block, its beats and their signature (the <c>structure</c> jsonb column).</summary>
    public IdeaStructure Structure { get; private set; } = new(StoryStructureTags.Empty, [], string.Empty);

    /// <summary>The novelty measurement of the pick (the <c>novelty</c> jsonb column); null until <see cref="SetSignals"/>.</summary>
    public IdeaNoveltyDetail? Novelty { get; private set; }

    /// <summary>The final weighted score of the pick, 0…1; null until <see cref="SetSignals"/>.</summary>
    public decimal? Score { get; private set; }

    /// <summary>The similarity <c>max(lexical, judge)</c> the run log shows, 0…1; null until <see cref="SetSignals"/>.</summary>
    public decimal? Similarity { get; private set; }

    /// <summary>The lifecycle status; see the class remarks for the only allowed transitions.</summary>
    public IdeaStatus Status { get; private set; }

    /// <summary>Why the idea was rejected (one of <see cref="IdeaRejectionReason"/>); null unless rejected.</summary>
    public string? RejectedReason { get; private set; }

    /// <summary>The episode the picked idea became; null until <see cref="MarkUsed"/>.</summary>
    public Guid? EpisodeId { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Creates a <see cref="IdeaStatus.Proposed"/> idea from one validated candidate of an <c>ideas</c> run.
    /// </summary>
    /// <remarks>
    /// The candidate has already passed <see cref="IdeaValidator"/>, so this factory does not re-check the
    /// content rules — a violating candidate is dropped by the caller, never repaired here — and it guards
    /// only what a stored row must satisfy: an owner, a series, the run and four non-blank texts. A caller
    /// that skipped the validator is the programmer error the blanks throw for, not user input.
    /// </remarks>
    /// <param name="seriesId">The series the idea belongs to.</param>
    /// <param name="userId">The owner of the series; also the owner of the idea.</param>
    /// <param name="generationRunId">The <c>ideas</c> run that produced the candidate.</param>
    /// <param name="candidate">The validated candidate.</param>
    /// <param name="clock">The clock reading of the insert.</param>
    /// <returns>The new idea, <see cref="IdeaStatus.Proposed"/>, unscored and without an episode.</returns>
    /// <exception cref="ArgumentException">An id is empty or one of the four texts is blank.</exception>
    public static EpisodeIdea CreateProposed(
        Guid seriesId,
        Guid userId,
        Guid generationRunId,
        IdeaCandidate candidate,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(clock);

        if (seriesId == Guid.Empty)
        {
            throw new ArgumentException("An episode idea needs its series.", nameof(seriesId));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("An episode idea needs its owner.", nameof(userId));
        }

        if (generationRunId == Guid.Empty)
        {
            throw new ArgumentException("An episode idea needs the run that proposed it.", nameof(generationRunId));
        }

        return new EpisodeIdea
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            SeriesId = seriesId,
            GenerationRunId = generationRunId,
            Title = Required(candidate.Title, "title"),
            Logline = Required(candidate.Logline, "logline"),
            Hook = Required(candidate.Hook, "hook"),
            Setting = Required(candidate.Setting, "setting"),
            Structure = IdeaStructure.From(candidate),
            Status = IdeaStatus.Proposed,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };
    }

    /// <summary>
    /// Marks the idea as the run's pick and stores the score it won with:
    /// <see cref="IdeaStatus.Proposed"/> → <see cref="IdeaStatus.Picked"/>.
    /// </summary>
    /// <param name="score">The weighted score of 02.8.1 plan §3.4, already clamped to 0…1.</param>
    /// <param name="clock">The scoring step's clock reading.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="score"/> is outside 0…1.</exception>
    /// <exception cref="InvalidOperationException">The idea is rejected or used, or already picked.</exception>
    public void MarkPicked(decimal score, IClock clock)
    {
        EnsureScore(score, nameof(score));
        RequireStatus(IdeaStatus.Proposed, "picked");
        ArgumentNullException.ThrowIfNull(clock);

        Status = IdeaStatus.Picked;
        Score = score;
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>
    /// Marks the idea as rejected with one of the <see cref="IdeaRejectionReason"/> values:
    /// <see cref="IdeaStatus.Proposed"/> → <see cref="IdeaStatus.Rejected"/>.
    /// </summary>
    /// <remarks>
    /// Rejecting an already rejected idea with the same reason is a no-op, so a retried scoring step answers
    /// the same way; a different reason on a rejected row is a programming error, because the first reason is
    /// the one the run log showed.
    /// </remarks>
    /// <param name="reason">The rejection reason; one of the <see cref="IdeaRejectionReason"/> constants.</param>
    /// <param name="clock">The scoring step's clock reading.</param>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is blank or longer than <see cref="RejectedReasonMaxChars"/>.</exception>
    /// <exception cref="InvalidOperationException">The idea is picked, used, or rejected with another reason.</exception>
    public void MarkRejected(string reason, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var rejection = Required(reason, nameof(reason));

        if (rejection.Length > RejectedReasonMaxChars)
        {
            throw new ArgumentException(
                $"A rejection reason is at most {RejectedReasonMaxChars} characters.",
                nameof(reason));
        }

        if (Status == IdeaStatus.Rejected && string.Equals(RejectedReason, rejection, StringComparison.Ordinal))
        {
            return;
        }

        RequireStatus(IdeaStatus.Proposed, "rejected");

        Status = IdeaStatus.Rejected;
        RejectedReason = rejection;
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>
    /// Stores the measurements the pick was ranked on: the two scores and the novelty document. Called for
    /// every scored candidate, so it does not change <see cref="Status"/>.
    /// </summary>
    /// <param name="score">The weighted score, 0…1 — the same value <see cref="MarkPicked"/> took.</param>
    /// <param name="similarity">The similarity the run log shows, 0…1 (<c>max(lexical, judge)</c>).</param>
    /// <param name="novelty">The novelty document of the row.</param>
    /// <param name="clock">The scoring step's clock reading.</param>
    /// <exception cref="ArgumentNullException"><paramref name="novelty"/> or <paramref name="clock"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A score is outside 0…1.</exception>
    public void SetSignals(decimal score, decimal similarity, IdeaNoveltyDetail novelty, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(novelty);
        ArgumentNullException.ThrowIfNull(clock);
        EnsureScore(score, nameof(score));
        EnsureScore(similarity, nameof(similarity));

        Score = score;
        Similarity = similarity;
        Novelty = novelty;
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>
    /// Records that the picked idea became an episode: <see cref="IdeaStatus.Picked"/> →
    /// <see cref="IdeaStatus.Used"/>. Called by 02.8.3 after the episode was created.
    /// </summary>
    /// <remarks>
    /// This is the one transition an HTTP command drives, so a second call — or a call for an idea that is
    /// still proposed or was rejected — answers <see cref="InvalidStateCode"/> instead of throwing. The
    /// table's partial unique index on <c>episode_id</c> is the same invariant one level down: one idea per
    /// episode.
    /// </remarks>
    /// <param name="episodeId">The episode the idea became.</param>
    /// <param name="clock">The calling step's clock reading.</param>
    /// <returns>Success, or <see cref="InvalidStateCode"/> when the idea is not picked.</returns>
    /// <exception cref="ArgumentException"><paramref name="episodeId"/> is empty.</exception>
    public ErrorOr<Success> MarkUsed(Guid episodeId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (episodeId == Guid.Empty)
        {
            throw new ArgumentException("A used idea needs its episode.", nameof(episodeId));
        }

        if (Status != IdeaStatus.Picked)
        {
            return Error.Conflict(
                InvalidStateCode,
                "Only a picked idea can be marked used.");
        }

        Status = IdeaStatus.Used;
        EpisodeId = episodeId;
        UpdatedAt = clock.UtcNow;

        return Result.Success;
    }

    /// <summary>Throws unless the idea is in <paramref name="required"/>, the status the transition starts from.</summary>
    private void RequireStatus(IdeaStatus required, string transition)
    {
        if (Status != required)
        {
            throw new InvalidOperationException(
                $"An idea can be {transition} only from {required}; this one is {Status}.");
        }
    }

    /// <summary>Guards a score column value: the column is <c>numeric(5,4)</c>, so the domain is 0…1.</summary>
    private static void EnsureScore(decimal value, string parameterName)
    {
        if (value is < 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "A score is between 0 and 1.");
        }
    }

    /// <summary>A required trimmed string: blank is refused, the value is stored as written.</summary>
    private static string Required(string? value, string parameterName)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ArgumentException($"'{parameterName}' is required.", parameterName);
        }

        return trimmed;
    }
}
