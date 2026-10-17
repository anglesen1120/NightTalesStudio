using ErrorOr;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Autopilot.Ideas;

namespace NightTales.Application.Features.Autopilot.Ideas;

/// <summary>
/// The errors of the idea steps (02.8.1-T1 plan §4, README D7/D8): the tenant answers and the mapped provider
/// outcomes the caller (02.8.3-T1) records on its <c>generation_runs</c> row.
/// </summary>
/// <remarks>
/// <para>
/// The four generation codes are the confirmed D8 additions of TL ruling #3 (2026-09-24);
/// <c>provider_funds_exhausted</c> is D7's shared pause code. They are returned as data, never thrown: the
/// caller decides the run outcome and the pause, this feature only names the case.
/// </para>
/// <para>
/// The descriptions are safe text: no idea, bible or note content and no provider message ever reaches them
/// (D19).
/// </para>
/// </remarks>
public static class IdeaErrors
{
    /// <summary>
    /// 404: the series is unknown, or it belongs to another user (09 README D10: the unknown, the foreign and
    /// the deleted all answer the same code, so nothing leaks through the difference).
    /// </summary>
    public static Error SeriesNotFound { get; } =
        Error.NotFound(ErrorCodes.NotFound, "The series does not exist.");

    /// <summary>404: the idea is unknown, or it belongs to another user.</summary>
    public static Error IdeaNotFound { get; } =
        Error.NotFound(ErrorCodes.NotFound, "The episode idea does not exist.");

    /// <summary>
    /// 404: the episode the outcome asked to link is unknown, or it belongs to another user — the same-owner key
    /// <c>fk_episode_ideas_episodes_episode_id_user_id</c> refused the link. It reads exactly like a lookup of that
    /// episode, so a cross-tenant link is never observable (09 README D10).
    /// </summary>
    public static Error EpisodeNotFound { get; } =
        Error.NotFound(ErrorCodes.NotFound, "The episode does not exist.");

    /// <summary>
    /// The run failed <c>ideas_invalid_output</c> (README D8): the model answered twice with something the strict
    /// schema or <c>IdeaValidator</c> refused, and no valid candidate survived.
    /// </summary>
    public static Error InvalidOutput { get; } =
        Error.Failure(ErrorCodes.IdeasInvalidOutput, "The model returned no valid episode idea.");

    /// <summary>
    /// The run failed <c>ideas_moderated</c> (README D8, D12): the provider refused or filtered the call. One
    /// <c>moderation_events</c> row with <c>stage = ideas</c> is written before this is answered.
    /// </summary>
    public static Error Moderated { get; } =
        Error.Failure(ErrorCodes.IdeasModerated, "The provider refused to generate episode ideas for this series.");

    /// <summary>
    /// The run failed <c>ideas_generation_failed</c> (README D8): the provider answered an error, or was
    /// unavailable past its retry budget.
    /// </summary>
    public static Error GenerationFailed { get; } =
        Error.Failure(ErrorCodes.IdeasGenerationFailed, "The provider could not generate episode ideas.");

    /// <summary>
    /// The run failed <c>provider_funds_exhausted</c> (README D7): the provider account is out of funds or at its
    /// spend limit. 02.8.3-T1 pauses the series on this one; it is never metered, because no request was sent.
    /// </summary>
    public static Error ProviderFundsExhausted { get; } =
        Error.Failure(ErrorCodes.ProviderFundsExhausted, "The provider account has no funds left.");

    /// <summary>
    /// 409: the episode already has an idea — the partial unique index <c>UX_episode_ideas_episode</c> refused the
    /// write — or this idea cannot be used (it is not picked, or it is already used). The two are one class of
    /// answer: the state of the idea/episode pair does not allow the transition. 02.8.3-T1 treats it as a bug
    /// rather than a user error (plan §4 U4).
    /// </summary>
    public static Error EpisodeAlreadyUsed { get; } =
        Error.Conflict(EpisodeIdea.InvalidStateCode, "This episode already has an idea, or the idea cannot be used.");
}
