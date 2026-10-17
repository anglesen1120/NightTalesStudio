using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Text;

namespace NightTales.Application.Features.Autopilot.Ideas;

/// <summary>
/// The fault vocabulary and the two fault decisions the idea step's paid calls share (02.8.1-T1/T2): the
/// failure details the run log records, the truncation test that decides whether an invalid output earns the
/// run's one retry, and the refusal evidence a <c>Moderated</c> outcome writes (D12, TL ruling 2026-09-24).
/// </summary>
/// <remarks>
/// The ideas call and the novelty-judge call answer to the same failure table (task file "Failure handling"), so
/// both must name a fault the same way and evidence a refusal the same way: one <c>moderation_events</c> row with
/// <c>stage = ideas</c> — never <c>brief</c>, which is the per-candidate pre-check — and never with the model's
/// own text as evidence (D19). Kept in one place so the two call sites cannot drift on either.
/// </remarks>
internal static class IdeaCallFaults
{
    /// <summary>The failure detail of a provider that answered 5xx or timed out past its retry budget.</summary>
    public const string ProviderUnavailable = "provider_unavailable";

    /// <summary>The failure detail of a provider that refused the request with a non-retryable status.</summary>
    public const string ProviderError = "provider_error";

    /// <summary>The failure detail of a provider that failed before its call was dispatched.</summary>
    public const string Unexpected = "unexpected_error";

    /// <summary>The failure detail of a provider that refused or filtered the call (D12's refusal).</summary>
    public const string ProviderRefused = "provider_refused";

    /// <summary>The failure detail of a provider account with no funds left (never metered, D7).</summary>
    public const string FundsExhausted = "funds_exhausted";

    /// <summary>The failure detail of a second output the schema or the validator refused.</summary>
    public const string InvalidOutputTwice = "invalid_output_twice";

    /// <summary>The failure detail of an output cut short by the ruled <c>max_output_tokens</c> (no retry: D7).</summary>
    public const string InvalidOutputMaxTokens = "invalid_output_max_tokens";

    /// <summary>The failure detail of a retry the platform spend cap refused before dispatch (D1).</summary>
    public const string SpendCapReached = "spend_cap_reached";

    /// <summary>The failure detail of a text-call outcome this table does not name (D7's unmodelled failure).</summary>
    public const string UnmappedOutcome = "unmapped_outcome";

    /// <summary>The write budget of the refusal evidence, which must survive a caller that gave up (class 3).</summary>
    private static readonly TimeSpan ModerationWriteTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether an invalid output was cut short by the ruled <c>max_output_tokens</c>. A truncated answer is final
    /// on the first call: a retry would be cut at the same cap (D7).
    /// </summary>
    /// <typeparam name="TDraft">The document type of the refused call.</typeparam>
    /// <param name="invalid">The refused output's outcome.</param>
    /// <returns><see langword="true"/> when the answer hit the output ceiling.</returns>
    public static bool IsTruncated<TDraft>(TextCallInvalidOutput<TDraft> invalid)
        where TDraft : class =>
        invalid.RuleIds.Contains(TextCallInvalidOutput<TDraft>.MaxOutputTokensExceeded);

    /// <summary>
    /// Writes the one <c>moderation_events</c> row of a paid call the provider refused or filtered, on its own
    /// budget and its own scope: the writer swallows its own failures, so the evidence never changes the run's
    /// outcome.
    /// </summary>
    /// <param name="writer">The one writer of <c>moderation_events</c>.</param>
    /// <param name="run">The run the refusal belongs to; it is series-level, so the episode is null.</param>
    /// <param name="categories">The flagged categories when the provider named them; empty otherwise.</param>
    /// <returns>A task that completes when the row is saved or the failure is logged.</returns>
    public static async ValueTask WriteRefusalAsync(
        IModerationEventWriter writer,
        RunContext run,
        IReadOnlyList<string> categories)
    {
        using var terminal = new CancellationTokenSource(ModerationWriteTimeout);
        await writer
            .WriteAsync(
                run.UserId,
                run.EpisodeId,
                ModerationSource.OpenaiText,
                ModerationStage.Ideas,
                categories,
                // D19: the refusal is about our own prompt and the model's answer, and neither may be stored as
                // evidence text — the categories and the run are the whole record.
                ownText: null,
                terminal.Token)
            .ConfigureAwait(false);
    }
}
