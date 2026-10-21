using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;
using NightTales.Application.Common.Errors;

namespace NightTales.Api.Http;

/// <summary>
/// The closed catalogue behind every <c>ErrorOr</c> result (09 README D7, 09 §5.4). The HTTP status
/// comes from <see cref="Status"/>, keyed by code, never from <c>ErrorType</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every constant of <see cref="ErrorCodes"/> appears in exactly one of three frozen sets, so that a
/// code can never be mapped to a status it must not have:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="Status"/> — the codes that answer an HTTP error; the value is the
/// status from the §5.4 HTTP column.</description></item>
/// <item><description><see cref="AcceptedBodyCodes"/> — 202-body-only success codes. They are
/// deliberately absent from <see cref="Status"/>: a handler that returns one as an <em>error</em>
/// hits the unknown-code path and fails loudly with 500 <c>internal_error</c> instead of answering
/// a wrong status.</description></item>
/// <item><description><see cref="NonHttpCodes"/> — job-outcome and job-state codes, Worker-internal
/// codes, provider reasons and the 302-only redirect values. They never become an HTTP response
/// body, so they carry no status.</description></item>
/// </list>
/// <para>
/// <c>setting_change_pending</c> and <c>owner_action_disputed</c> are dual: §5.4 lists them as
/// "202 body / 409". They live in <see cref="Status"/> with 409, because the mapper's direction is
/// the error one — a code in <see cref="AcceptedBodyCodes"/> could never be mapped, so an error
/// carrying it would answer 500. The three sets therefore stay disjoint while both readings of those
/// two rows hold.
/// </para>
/// <para>
/// <c>internal_error</c> is not a §5.4 row, but the mapping rules require it (unknown code, unhandled
/// exception); it is catalogued with 500 here so a handler may also return it on purpose.
/// </para>
/// <para>
/// <c>unauthorized</c> is 401 and is likewise not a §5.4 row: §5.4 lists 401 only for the specific
/// reasons (<c>invalid_credentials</c>, <c>mfa_required</c>, <c>session_revoked</c>, …), while
/// <c>09.1.3-T2</c>'s <c>AuthorizationBehavior</c> returns a plain <c>Error.Unauthorized</c> for a
/// default-denied call. Catalogued here as 09 CR-38.
/// </para>
/// <para>
/// This type is public because the invariant test lives in <c>NightTales.Api.IntegrationTests</c>;
/// there is no <c>InternalsVisibleTo</c> in this solution.
/// </para>
/// </remarks>
public static class ErrorCatalogue
{
    /// <summary>
    /// The host of every problem <c>type</c> URI, <c>https://nighttales.app/errors/&lt;code&gt;</c>.
    /// The public domain is a placeholder until 11 fixes it (09 README D7), so it lives here once.
    /// </summary>
    public const string ProblemTypeBase = "https://nighttales.app/errors/";

    /// <summary>
    /// Code → HTTP status, from the §5.4 HTTP column plus the D17 additions, <c>internal_error</c>
    /// and <c>unauthorized</c> (09 CR-38).
    /// </summary>
    public static readonly FrozenDictionary<string, int> Status =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            // 400
            [ErrorCodes.ValidationFailed] = StatusCodes.Status400BadRequest,
            [ErrorCodes.CaptchaRequired] = StatusCodes.Status400BadRequest,
            [ErrorCodes.CaptchaInvalid] = StatusCodes.Status400BadRequest,
            [ErrorCodes.BreachedPassword] = StatusCodes.Status400BadRequest,
            [ErrorCodes.VoiceLanguageMismatch] = StatusCodes.Status400BadRequest,
            [ErrorCodes.PaymentMethodUnavailable] = StatusCodes.Status400BadRequest,
            [ErrorCodes.PackageBelowCost] = StatusCodes.Status400BadRequest,
            [ErrorCodes.PromoInvalid] = StatusCodes.Status400BadRequest,
            [ErrorCodes.PromoExpired] = StatusCodes.Status400BadRequest,
            [ErrorCodes.IdempotencyKeyRequired] = StatusCodes.Status400BadRequest,
            [ErrorCodes.StoryAttestationRequired] = StatusCodes.Status400BadRequest,
            [ErrorCodes.StoryInvalidContent] = StatusCodes.Status400BadRequest,
            [ErrorCodes.FactSheetInvalidContent] = StatusCodes.Status400BadRequest,
            [ErrorCodes.BibleInvalidContent] = StatusCodes.Status400BadRequest,
            [ErrorCodes.SourceUrlInvalid] = StatusCodes.Status400BadRequest,
            [ErrorCodes.SourceDomainNotAllowed] = StatusCodes.Status400BadRequest,
            [ErrorCodes.AutopilotSettingsInvalid] = StatusCodes.Status400BadRequest,
            [ErrorCodes.SettingInvalid] = StatusCodes.Status400BadRequest,
            [ErrorCodes.SettingSecretRequired] = StatusCodes.Status400BadRequest,
            [ErrorCodes.CancelTokenInvalid] = StatusCodes.Status400BadRequest,
            [ErrorCodes.EmailTemplateUnknown] = StatusCodes.Status400BadRequest,
            [ErrorCodes.FetchTaskKindUnsupported] = StatusCodes.Status400BadRequest,
            [ErrorCodes.EmailTemplateKeys] = StatusCodes.Status400BadRequest,
            [ErrorCodes.AgeAttestationRequired] = StatusCodes.Status400BadRequest,
            [ErrorCodes.ReportTargetInvalid] = StatusCodes.Status400BadRequest,
            [ErrorCodes.ConsentTextOutdated] = StatusCodes.Status400BadRequest,
            [ErrorCodes.TiktokInvalidPostSettings] = StatusCodes.Status400BadRequest,
            [ErrorCodes.TiktokVideoTooLong] = StatusCodes.Status400BadRequest,
            [ErrorCodes.OauthStateInvalid] = StatusCodes.Status400BadRequest,

            // 05.1.2-T1's RenderSubmitMapper codes (09.6.1-T1): the submit runs in the Worker, so these are
            // internal; catalogued as 400 so a future submit surface (or the render command, which answers
            // render_script_too_long as validation_failed field `script`) maps them consistently.
            [ErrorCodes.RenderAspectInvalid] = StatusCodes.Status400BadRequest,
            [ErrorCodes.RenderSubjectInvalid] = StatusCodes.Status400BadRequest,
            [ErrorCodes.RenderScriptTooLong] = StatusCodes.Status400BadRequest,
            [ErrorCodes.RenderTermsInvalid] = StatusCodes.Status400BadRequest,

            // 401
            [ErrorCodes.Unauthorized] = StatusCodes.Status401Unauthorized,
            [ErrorCodes.InvalidCredentials] = StatusCodes.Status401Unauthorized,
            [ErrorCodes.MfaRequired] = StatusCodes.Status401Unauthorized,
            [ErrorCodes.InvalidMfaCode] = StatusCodes.Status401Unauthorized,
            [ErrorCodes.MfaTokenInvalid] = StatusCodes.Status401Unauthorized,
            [ErrorCodes.SessionRevoked] = StatusCodes.Status401Unauthorized,
            [ErrorCodes.HubTicketInvalid] = StatusCodes.Status401Unauthorized,
            [ErrorCodes.RefreshTokenReused] = StatusCodes.Status401Unauthorized,
            [ErrorCodes.ReauthRequired] = StatusCodes.Status401Unauthorized,
            [ErrorCodes.MfaEnrolmentRequired] = StatusCodes.Status401Unauthorized,
            [ErrorCodes.WebhookSignatureInvalid] = StatusCodes.Status401Unauthorized,

            // 402
            [ErrorCodes.InsufficientCredits] = StatusCodes.Status402PaymentRequired,

            // 403
            [ErrorCodes.EmailNotVerified] = StatusCodes.Status403Forbidden,
            [ErrorCodes.Forbidden] = StatusCodes.Status403Forbidden,
            [ErrorCodes.RegistrationClosed] = StatusCodes.Status403Forbidden,
            [ErrorCodes.AccountUnderReview] = StatusCodes.Status403Forbidden,
            [ErrorCodes.SelfGrantForbidden] = StatusCodes.Status403Forbidden,
            [ErrorCodes.GrantTargetForbidden] = StatusCodes.Status403Forbidden,

            // 404
            [ErrorCodes.NotFound] = StatusCodes.Status404NotFound,
            [ErrorCodes.FeatureDisabled] = StatusCodes.Status404NotFound,
            [ErrorCodes.PackageNotFound] = StatusCodes.Status404NotFound,
            [ErrorCodes.FetchTaskNotFound] = StatusCodes.Status404NotFound,
            [ErrorCodes.FactSheetNotFound] = StatusCodes.Status404NotFound,

            // 409
            [ErrorCodes.EpisodeInvalidState] = StatusCodes.Status409Conflict,
            [ErrorCodes.FetchLeaseLost] = StatusCodes.Status409Conflict,
            [ErrorCodes.ConcurrencyConflict] = StatusCodes.Status409Conflict,
            [ErrorCodes.SeriesArchived] = StatusCodes.Status409Conflict,
            [ErrorCodes.SeriesTopicLocked] = StatusCodes.Status409Conflict,
            [ErrorCodes.StoryGenerationInProgress] = StatusCodes.Status409Conflict,
            [ErrorCodes.FactSheetGenerationInProgress] = StatusCodes.Status409Conflict,
            [ErrorCodes.FindSourcesInProgress] = StatusCodes.Status409Conflict,
            [ErrorCodes.FactSheetRequired] = StatusCodes.Status409Conflict,
            [ErrorCodes.StorySourcesRequired] = StatusCodes.Status409Conflict,
            [ErrorCodes.StoryFactSheetOutdated] = StatusCodes.Status409Conflict,
            [ErrorCodes.StoryTooSimilarToSource] = StatusCodes.Status409Conflict,
            [ErrorCodes.StoryModerated] = StatusCodes.Status409Conflict,
            [ErrorCodes.FactSheetSourcesInsufficient] = StatusCodes.Status409Conflict,
            [ErrorCodes.SourceDuplicate] = StatusCodes.Status409Conflict,
            [ErrorCodes.SourceLimitReached] = StatusCodes.Status409Conflict,
            [ErrorCodes.SourceCandidateNotProposed] = StatusCodes.Status409Conflict,
            [ErrorCodes.BibleProposalNotPending] = StatusCodes.Status409Conflict,
            [ErrorCodes.AutopilotAuthorizationRequired] = StatusCodes.Status409Conflict,
            [ErrorCodes.AutopilotTopicLocked] = StatusCodes.Status409Conflict,
            [ErrorCodes.AutopilotPauseReasonActive] = StatusCodes.Status409Conflict,

            // 02.8.1-T2's idea state machine: an idea that is not `picked` cannot be linked to an episode.
            [ErrorCodes.IdeaInvalidState] = StatusCodes.Status409Conflict,
            [ErrorCodes.KeywordsFlagged] = StatusCodes.Status409Conflict,
            [ErrorCodes.ChannelAlreadyConnected] = StatusCodes.Status409Conflict,
            [ErrorCodes.ChannelReauthRequired] = StatusCodes.Status409Conflict,
            [ErrorCodes.PublicationConsentRequired] = StatusCodes.Status409Conflict,
            [ErrorCodes.YoutubeRejected] = StatusCodes.Status409Conflict,
            [ErrorCodes.TiktokCannotPostNow] = StatusCodes.Status409Conflict,
            [ErrorCodes.SettingChangePending] = StatusCodes.Status409Conflict,
            [ErrorCodes.SettingChangeNotPending] = StatusCodes.Status409Conflict,
            [ErrorCodes.OwnerActionNotPending] = StatusCodes.Status409Conflict,
            [ErrorCodes.OwnerActionDisputed] = StatusCodes.Status409Conflict,
            [ErrorCodes.CancelLimitReached] = StatusCodes.Status409Conflict,
            [ErrorCodes.OwnerDisputeFrozen] = StatusCodes.Status409Conflict,
            [ErrorCodes.LastOwner] = StatusCodes.Status409Conflict,
            [ErrorCodes.LegalAcceptanceRequired] = StatusCodes.Status409Conflict,
            [ErrorCodes.LegalVersionOutdated] = StatusCodes.Status409Conflict,
            [ErrorCodes.LegalContentMissing] = StatusCodes.Status409Conflict,
            [ErrorCodes.PublicationInvalidState] = StatusCodes.Status409Conflict,
            [ErrorCodes.PublicationPrivacyLocked] = StatusCodes.Status409Conflict,
            [ErrorCodes.ChannelNotActive] = StatusCodes.Status409Conflict,
            [ErrorCodes.PaymentExpired] = StatusCodes.Status409Conflict,
            [ErrorCodes.PaymentInvalidState] = StatusCodes.Status409Conflict,
            [ErrorCodes.GrantInProgress] = StatusCodes.Status409Conflict,
            [ErrorCodes.PaymentAlreadyResolved] = StatusCodes.Status409Conflict,
            [ErrorCodes.RefundRequestNotPending] = StatusCodes.Status409Conflict,
            [ErrorCodes.GrantRequestNotPending] = StatusCodes.Status409Conflict,
            [ErrorCodes.RefundNotEligible] = StatusCodes.Status409Conflict,
            [ErrorCodes.PromoExhausted] = StatusCodes.Status409Conflict,
            [ErrorCodes.PromoAlreadyUsed] = StatusCodes.Status409Conflict,

            // 412
            [ErrorCodes.EpisodeConflict] = StatusCodes.Status412PreconditionFailed,
            [ErrorCodes.StoryVersionConflict] = StatusCodes.Status412PreconditionFailed,
            [ErrorCodes.FactSheetConflict] = StatusCodes.Status412PreconditionFailed,
            [ErrorCodes.BibleConflict] = StatusCodes.Status412PreconditionFailed,
            [ErrorCodes.SeriesConflict] = StatusCodes.Status412PreconditionFailed,
            [ErrorCodes.SettingConflict] = StatusCodes.Status412PreconditionFailed,
            [ErrorCodes.PublicationConflict] = StatusCodes.Status412PreconditionFailed,

            // 413
            [ErrorCodes.PayloadTooLarge] = StatusCodes.Status413PayloadTooLarge,

            // 422
            [ErrorCodes.IdempotencyKeyReused] = StatusCodes.Status422UnprocessableEntity,

            // 428
            [ErrorCodes.IfMatchRequired] = StatusCodes.Status428PreconditionRequired,

            // 429 (with Retry-After where the error carries retryAfterSeconds, 02 CR-20)
            [ErrorCodes.StoryGenerationLimit] = StatusCodes.Status429TooManyRequests,
            [ErrorCodes.SourceFetchLimitReached] = StatusCodes.Status429TooManyRequests,
            [ErrorCodes.YoutubeQuotaExhausted] = StatusCodes.Status429TooManyRequests,
            [ErrorCodes.TiktokCreatorCap] = StatusCodes.Status429TooManyRequests,
            [ErrorCodes.TiktokRateLimited] = StatusCodes.Status429TooManyRequests,
            [ErrorCodes.RateLimited] = StatusCodes.Status429TooManyRequests,
            [ErrorCodes.MfaLockedUntrusted] = StatusCodes.Status429TooManyRequests,
            [ErrorCodes.SpendCapReached] = StatusCodes.Status429TooManyRequests,

            // 500, 502, 503
            [ErrorCodes.InternalError] = StatusCodes.Status500InternalServerError,
            [ErrorCodes.SeriesBibleMissing] = StatusCodes.Status500InternalServerError,
            [ErrorCodes.SettingTestFailed] = StatusCodes.Status502BadGateway,
            [ErrorCodes.PaymentProviderError] = StatusCodes.Status502BadGateway,
            [ErrorCodes.YoutubeUploadFailed] = StatusCodes.Status502BadGateway,
            [ErrorCodes.TiktokUploadFailed] = StatusCodes.Status502BadGateway,
            [ErrorCodes.CaptchaUnavailable] = StatusCodes.Status503ServiceUnavailable,
            [ErrorCodes.ServiceUnavailable] = StatusCodes.Status503ServiceUnavailable,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// 202-body-only success codes (09 §2 E3). They never appear in a ProblemDetails body; a handler
    /// that returns one as an error fails the unknown-code path on purpose.
    /// </summary>
    public static readonly FrozenSet<string> AcceptedBodyCodes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            ErrorCodes.GrantRequiresApproval,
            ErrorCodes.OwnerActionPending,
            ErrorCodes.ObjectionRecorded,
        }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Codes that never become an HTTP response: job outcomes (<c>render_jobs.error_code</c>,
    /// <c>publications.error_code</c>, <c>generation_runs.error_code</c>), the cancelled job state,
    /// Worker-internal reasons and the 302-only TikTok redirect values.
    /// </summary>
    public static readonly FrozenSet<string> NonHttpCodes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            ErrorCodes.AutopilotUserRequired,
            ErrorCodes.AutopilotRunNotFound,
            ErrorCodes.AutopilotStepFailed,
            ErrorCodes.ImageModerated,
            ErrorCodes.ImageGenerationFailed,
            ErrorCodes.TtsFailed,
            ErrorCodes.RenderQualityFailed,
            ErrorCodes.RenderNodeError,
            ErrorCodes.RenderAssemblyFailed,
            ErrorCodes.RenderLost,
            ErrorCodes.RenderTimeout,
            ErrorCodes.RenderOutputInvalid,
            ErrorCodes.RenderCancelled,
            ErrorCodes.RenderWorkerUnavailable,
            ErrorCodes.EmailUserGone,
            ErrorCodes.EmailSendFailed,
            ErrorCodes.EmailSendRejected,
            ErrorCodes.ProviderFundsExhausted,
            ErrorCodes.TiktokActiveUserCap,
            ErrorCodes.TiktokCreatorBanned,
            ErrorCodes.TiktokContentRejected,
            ErrorCodes.TiktokVideoInvalid,
            ErrorCodes.TiktokPollTimeout,
            ErrorCodes.TiktokReauthRequired,
            ErrorCodes.PlatformIncident,
            ErrorCodes.OauthDenied,
            ErrorCodes.OauthExchangeFailed,
            ErrorCodes.TiktokScopeMissing,
            ErrorCodes.StoryInvalidOutput,
            ErrorCodes.StoryGenerationFailed,
            ErrorCodes.StoryGenerationCancelled,
            ErrorCodes.FactSheetInvalidOutput,
            ErrorCodes.FactSheetModerated,
            ErrorCodes.FactSheetGenerationFailed,
            ErrorCodes.BibleProposalInvalidOutput,
            ErrorCodes.BibleProposalModerated,
            ErrorCodes.BibleProposalGenerationFailed,
            ErrorCodes.IdeasInvalidOutput,
            ErrorCodes.IdeasModerated,
            ErrorCodes.IdeasGenerationFailed,
            ErrorCodes.IdeasNoIdea,
            ErrorCodes.FindSourcesGenerationFailed,
            ErrorCodes.SourceUnreachable,
            ErrorCodes.SourceTooLarge,
            ErrorCodes.SourceBlockedByRobots,
            ErrorCodes.SourceUnsupportedType,
            ErrorCodes.SourceNoText,
            ErrorCodes.PriceCatalogEmpty,
            ErrorCodes.PromptNotShipped,
            ErrorCodes.PromptTooLarge,

            // 05.1.2-T1's embedded subtitle-presets.v1 catalog (09.6.1-T1): server-side config faults — the
            // composition root fails fast on a bad file — so they never become an HTTP response body.
            ErrorCodes.SubtitlePresetsResourceMissing,
            ErrorCodes.SubtitlePresetsInvalid,
            ErrorCodes.SchemaInvalid,
            ErrorCodes.RevisionNotPositive,
            ErrorCodes.AspectMissing,
            ErrorCodes.FontNameInvalid,
            ErrorCodes.FontSizeOutOfRange,
            ErrorCodes.StrokeWidthOutOfRange,
            ErrorCodes.ColorInvalid,
            ErrorCodes.SubtitlePositionUnknown,
            ErrorCodes.CustomPositionOutOfRange,
            ErrorCodes.MaxCharsPerLineInvalid,
            ErrorCodes.MaxLinesInvalid,
        }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The problem-details <c>type</c> URI of <paramref name="code"/>.</summary>
    public static string ProblemType(string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);

        return string.Concat(ProblemTypeBase, code);
    }
}
