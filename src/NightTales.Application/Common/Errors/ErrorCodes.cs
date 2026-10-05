namespace NightTales.Application.Common.Errors;

/// <summary>
/// Snake_case error codes (09 README D7). The HTTP status is resolved from the code catalogue
/// (<c>Api/Http/ErrorCatalogue.cs</c>), never from <c>ErrorType</c>.
/// </summary>
/// <remarks>
/// Sources, one constant per code and no invented code: 09 §5.4 (the catalogue), 09 README D17 (the
/// wave-1 additions), 02 README D8 (plan 02, HTTP table and job-outcome list) and 12 §5.3 (the
/// concrete codes behind the §5.4 <c>payment_*</c>, <c>package_*</c> and <c>promo_*</c> rows).
/// Later plans add their own codes here in the same commit as their catalogue row; the invariant test
/// <c>ErrorCodes_EveryConstantHasCatalogueEntry_AndViceVersa</c> keeps the two sides equal.
/// </remarks>
public static class ErrorCodes
{
    // ---- 400 ----

    /// <summary>A FluentValidation failure; mapped to 400 with errors grouped by camelCase field.</summary>
    public const string ValidationFailed = "validation_failed";

    /// <summary>3 failures for this (account, IP/device); solve the CAPTCHA and retry (09.3.3).</summary>
    public const string CaptchaRequired = "captcha_required";

    /// <summary>Turnstile token wrong, expired, replayed (<c>timeout-or-duplicate</c>) or missing (09.3.3).</summary>
    public const string CaptchaInvalid = "captcha_invalid";

    /// <summary>Password found in a breached-password source (09.3.1).</summary>
    public const string BreachedPassword = "breached_password";

    /// <summary>Series voice language differs from the series language (09.4.1).</summary>
    public const string VoiceLanguageMismatch = "voice_language_mismatch";

    /// <summary>Bank QR not supported by the active provider, fail-closed (12 CR-7).</summary>
    public const string PaymentMethodUnavailable = "payment_method_unavailable";

    /// <summary>Package price below cost (12.7.1).</summary>
    public const string PackageBelowCost = "package_below_cost";

    /// <summary>Promo code invalid (12.7.2).</summary>
    public const string PromoInvalid = "promo_invalid";

    /// <summary>Promo code expired (12.7.2).</summary>
    public const string PromoExpired = "promo_expired";

    /// <summary>Paid/platform POST without <c>Idempotency-Key</c> (09 README D6).</summary>
    public const string IdempotencyKeyRequired = "idempotency_key_required";

    /// <summary>True-crime approve without <c>attestation.accepted = true</c> and the current text version (02 D8).</summary>
    public const string StoryAttestationRequired = "story_attestation_required";

    /// <summary>Edit body fails <c>StoryDraftValidator</c> (02 D8).</summary>
    public const string StoryInvalidContent = "story_invalid_content";

    /// <summary>Fact-sheet PUT body fails validation (02 D8).</summary>
    public const string FactSheetInvalidContent = "fact_sheet_invalid_content";

    /// <summary>Bible PUT beyond limits (02 D8).</summary>
    public const string BibleInvalidContent = "bible_invalid_content";

    /// <summary>Source URL is not http(s), is an IP literal, carries credentials, uses a port outside {80, 443}, exceeds 2,048 chars or does not parse (02 D8).</summary>
    public const string SourceUrlInvalid = "source_url_invalid";

    /// <summary>Blocked source domain (02 D11); also a source job outcome on a redirect hop.</summary>
    public const string SourceDomainNotAllowed = "source_domain_not_allowed";

    /// <summary><c>PUT /series/{id}/autopilot</c> body fails validation (02 D8).</summary>
    public const string AutopilotSettingsInvalid = "autopilot_settings_invalid";

    /// <summary>Settings validation (09.10.2).</summary>
    public const string SettingInvalid = "setting_invalid";

    /// <summary>URL changed, rollback of a secret-bearing key, or a test against an unsaved URL without a newly entered secret (09.10.2).</summary>
    public const string SettingSecretRequired = "setting_secret_required";

    /// <summary>Cancel link token missing, used, expired, not matching, token path disabled, or unknown id — one uniform error (09.10.8).</summary>
    public const string CancelTokenInvalid = "cancel_token_invalid";

    /// <summary>Queueing an e-mail with a template name outside <c>EmailTemplateCatalog</c> (09.2.6-T3).</summary>
    public const string EmailTemplateUnknown = "email_template_unknown";

    /// <summary>A queued e-mail carries a render value outside the template's producer allow-list — including a token-bearing key such as <c>link</c>, which is minted at send time (09.2.6-T3, 09 README §B10 WT30).</summary>
    public const string EmailTemplateKeys = "email_template_keys";

    /// <summary>18+ attestation missing at registration (13.2.1-T2).</summary>
    public const string AgeAttestationRequired = "age_attestation_required";

    /// <summary><c>POST /reports</c> target reference does not resolve (13.3.4-T1).</summary>
    public const string ReportTargetInvalid = "report_target_invalid";

    /// <summary>Publication consent sent with an old <c>pc.v1</c> version (06.2.2-T1).</summary>
    public const string ConsentTextOutdated = "consent_text_outdated";

    /// <summary>TikTok post screen validation (07.2.2).</summary>
    public const string TiktokInvalidPostSettings = "tiktok_invalid_post_settings";

    /// <summary>TikTok post screen validation (07.2.2).</summary>
    public const string TiktokVideoTooLong = "tiktok_video_too_long";

    /// <summary>OAuth state missing or mismatched on channel connect (06/07).</summary>
    public const string OauthStateInvalid = "oauth_state_invalid";

    // ---- 401 ----

    /// <summary>Not authenticated: the pipeline's <c>AuthorizationBehavior</c> denies by default (09.1.3-T2). §5.4 lists 401 for the specific reasons only, so this row is 09 CR-38.</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>Login failed (no global account lock, 09.3.3).</summary>
    public const string InvalidCredentials = "invalid_credentials";

    /// <summary>Password ok, MFA code needed; the body carries <c>mfaToken</c> (09.3.2).</summary>
    public const string MfaRequired = "mfa_required";

    /// <summary>Wrong TOTP/recovery code (09.3.2).</summary>
    public const string InvalidMfaCode = "invalid_mfa_code";

    /// <summary>MFA login token expired, used or revoked (09.3.2).</summary>
    public const string MfaTokenInvalid = "mfa_token_invalid";

    /// <summary>Security stamp changed, refresh family (<c>sid</c>) revoked or account not active; refresh or sign in again (09.3.2).</summary>
    public const string SessionRevoked = "session_revoked";

    /// <summary>Hub ticket missing, expired, reused or stamp-mismatched (09.8.1).</summary>
    public const string HubTicketInvalid = "hub_ticket_invalid";

    /// <summary>Refresh token family revoked (09.3.2).</summary>
    public const string RefreshTokenReused = "refresh_token_reused";

    /// <summary>Sensitive admin action or channel connect needs password (+ TOTP when enrolled) within 5 min (09.10.4, 09.3.2).</summary>
    public const string ReauthRequired = "reauth_required";

    /// <summary>Owner/admin without MFA: only the enrolment routes answer until TOTP is verified (09.10.4-T1).</summary>
    public const string MfaEnrolmentRequired = "mfa_enrolment_required";

    /// <summary>Payment-provider webhook signature invalid (12 §5.3).</summary>
    public const string WebhookSignatureInvalid = "webhook_signature_invalid";

    // ---- 402, 403 ----

    /// <summary>Buy credits (12).</summary>
    public const string InsufficientCredits = "insufficient_credits";

    /// <summary>Verify e-mail first (09.3.1).</summary>
    public const string EmailNotVerified = "email_not_verified";

    /// <summary>Role not allowed for this action where 404 is not used (09 §5.4, 12.7.2).</summary>
    public const string Forbidden = "forbidden";

    /// <summary>Sign-ups closed by the owner (09.10.6).</summary>
    public const string RegistrationClosed = "registration_closed";

    /// <summary>Account under review: paid and publishing actions are held (12 §5.3).</summary>
    public const string AccountUnderReview = "account_under_review";

    /// <summary>Grant to own account (12.5.1).</summary>
    public const string SelfGrantForbidden = "self_grant_forbidden";

    /// <summary>Admin granting to an admin/owner account (09.10.4, 12.5.1).</summary>
    public const string GrantTargetForbidden = "grant_target_forbidden";

    // ---- 404 ----

    /// <summary>Also used for other users' resources (09 README D10).</summary>
    public const string NotFound = "not_found";

    /// <summary>Flag off (09.2.5).</summary>
    public const string FeatureDisabled = "feature_disabled";

    /// <summary>Unknown credit package (12 §5.3).</summary>
    public const string PackageNotFound = "package_not_found";

    // ---- 409 ----

    /// <summary>Transition not allowed (02 D8; replaces the retired <c>story_not_in_review</c>).</summary>
    public const string EpisodeInvalidState = "episode_invalid_state";

    /// <summary>Lost update detected by <c>xmin</c> after the <c>If-Match</c> check; retry (09 CR-7).</summary>
    public const string ConcurrencyConflict = "concurrency_conflict";

    /// <summary>Episode create on an archived series (09 CR-7).</summary>
    public const string SeriesArchived = "series_archived";

    /// <summary>Topic change while autopilot is enabled (09 CR-7, 02 D8).</summary>
    public const string SeriesTopicLocked = "series_topic_locked";

    /// <summary>One active generation run per episode and kind (02 D8).</summary>
    public const string StoryGenerationInProgress = "story_generation_in_progress";

    /// <summary>One active fact-sheet run per episode (02 D8).</summary>
    public const string FactSheetGenerationInProgress = "fact_sheet_generation_in_progress";

    /// <summary>Find sources while a <c>find_sources</c> run is active (02 D8).</summary>
    public const string FindSourcesInProgress = "find_sources_in_progress";

    /// <summary>True-crime generate without an approved sheet (02 D8).</summary>
    public const string FactSheetRequired = "fact_sheet_required";

    /// <summary>True-crime approve without ≥ 2 cited fetched sources (02 D8).</summary>
    public const string StorySourcesRequired = "story_sources_required";

    /// <summary>Approve when the story's <c>fact_sheet_id</c> differs from the currently approved sheet (02 D8).</summary>
    public const string StoryFactSheetOutdated = "story_fact_sheet_outdated";

    /// <summary>Approve with <c>similarity_score</c> above the block threshold (02 D8).</summary>
    public const string StoryTooSimilarToSource = "story_too_similar_to_source";

    /// <summary>Approve while the current version has a blocking moderation category; also a job outcome (02 D8).</summary>
    public const string StoryModerated = "story_moderated";

    /// <summary>Fact-sheet generate with 0 fetched sources; true-crime approve with &lt; 2 cited fetched sources (02 D8).</summary>
    public const string FactSheetSourcesInsufficient = "fact_sheet_sources_insufficient";

    /// <summary>Same normalised URL already on the episode (02 D8).</summary>
    public const string SourceDuplicate = "source_duplicate";

    /// <summary>More than 10 sources on the episode (02 D8).</summary>
    public const string SourceLimitReached = "source_limit_reached";

    /// <summary>Add/dismiss a candidate that is already added (02 D8).</summary>
    public const string SourceCandidateNotProposed = "source_candidate_not_proposed";

    /// <summary>Accept/dismiss without a pending bible proposal (02 D8).</summary>
    public const string BibleProposalNotPending = "bible_proposal_not_pending";

    /// <summary>Autopilot on without accepting the current authorization text (02 D8).</summary>
    public const string AutopilotAuthorizationRequired = "autopilot_authorization_required";

    /// <summary><c>PATCH /series</c> topic while autopilot is enabled (02 D8).</summary>
    public const string AutopilotTopicLocked = "autopilot_topic_locked";

    /// <summary>Resume while a pause trigger is still active (02 D8).</summary>
    public const string AutopilotPauseReasonActive = "autopilot_pause_reason_active";

    /// <summary>Render keyword linter at approve and render; the body carries <c>errors[] { segment, index, rule }</c> (03 D10).</summary>
    public const string KeywordsFlagged = "keywords_flagged";

    /// <summary>Channel connect: the platform account is already connected (06/07).</summary>
    public const string ChannelAlreadyConnected = "channel_already_connected";

    /// <summary>The user must reconnect the channel (10 CR-29).</summary>
    public const string ChannelReauthRequired = "channel_reauth_required";

    /// <summary>Consent missing for a publication (06.2.2).</summary>
    public const string PublicationConsentRequired = "publication_consent_required";

    /// <summary>YouTube rejected the upload (07 / 06).</summary>
    public const string YoutubeRejected = "youtube_rejected";

    /// <summary>Cannot post now — TikTok creator-info gate (07.3.3).</summary>
    public const string TiktokCannotPostNow = "tiktok_cannot_post_now";

    /// <summary>A critical setting change is saved as pending and a pending change already exists for that integration (09.10.2); also the 202 body code when the change is saved.</summary>
    public const string SettingChangePending = "setting_change_pending";

    /// <summary>Approve/cancel of a setting change that is already applied or cancelled (09.10.2).</summary>
    public const string SettingChangeNotPending = "setting_change_not_pending";

    /// <summary>Approve/cancel of an owner action that is already applied or cancelled (09.10.4).</summary>
    public const string OwnerActionNotPending = "owner_action_not_pending";

    /// <summary>A party tried to cancel a disputed request (09.10.4); the 202 body code when the new action is saved as disputed.</summary>
    public const string OwnerActionDisputed = "owner_action_disputed";

    /// <summary>The caller already cancelled this (action, target) 2 times in 30 days, or is the target and already used its single cancel (09.10.4).</summary>
    public const string CancelLimitReached = "cancel_limit_reached";

    /// <summary>The target was restored by the host operator in the last 30 days; only the operator can act on it (09.10.7).</summary>
    public const string OwnerDisputeFrozen = "owner_dispute_frozen";

    /// <summary>The last active owner cannot be demoted, suspended or deleted (09.10.4).</summary>
    public const string LastOwner = "last_owner";

    /// <summary>Current ToS + Privacy not accepted (13.2.1-T1).</summary>
    public const string LegalAcceptanceRequired = "legal_acceptance_required";

    /// <summary>The accepted legal version is not current (13.2.1-T1).</summary>
    public const string LegalVersionOutdated = "legal_version_outdated";

    /// <summary>The requested legal document/locale has no published row (13.2.2-T1).</summary>
    public const string LegalContentMissing = "legal_content_missing";

    /// <summary>Publication command in the wrong state (or a second active publication) (06 CR-9).</summary>
    public const string PublicationInvalidState = "publication_invalid_state";

    /// <summary>Privacy change on a pre-audit publication row (06 CR-9).</summary>
    public const string PublicationPrivacyLocked = "publication_privacy_locked";

    /// <summary>The channel connection is not <c>active</c> (06 CR-9).</summary>
    public const string ChannelNotActive = "channel_not_active";

    /// <summary>Payment expired (12.3.3).</summary>
    public const string PaymentExpired = "payment_expired";

    /// <summary>Payment in an invalid state for the command (12.3.3).</summary>
    public const string PaymentInvalidState = "payment_invalid_state";

    /// <summary>A grant for this target is already in progress (12.5.1).</summary>
    public const string GrantInProgress = "grant_in_progress";

    /// <summary>The payment was already resolved (12.3.3).</summary>
    public const string PaymentAlreadyResolved = "payment_already_resolved";

    /// <summary>Refund request already resolved — distinct from <c>refund_not_eligible</c> (BR28).</summary>
    public const string RefundRequestNotPending = "refund_request_not_pending";

    /// <summary>Approve/reject of a grant request that is no longer pending (12.5.1).</summary>
    public const string GrantRequestNotPending = "grant_request_not_pending";

    /// <summary>Refund not eligible under the refund policy (12 §5.3).</summary>
    public const string RefundNotEligible = "refund_not_eligible";

    /// <summary>Promo code exhausted (12 §5.3).</summary>
    public const string PromoExhausted = "promo_exhausted";

    /// <summary>Promo code already used by this account (12 §5.3).</summary>
    public const string PromoAlreadyUsed = "promo_already_used";

    // ---- 412 (stale If-Match) ----

    /// <summary>Stale <c>If-Match</c> on <c>DELETE /episodes/{id}</c> (09 CR-7).</summary>
    public const string EpisodeConflict = "episode_conflict";

    /// <summary>Edit/approve with a stale story ETag (02 D8).</summary>
    public const string StoryVersionConflict = "story_version_conflict";

    /// <summary>Fact-sheet edit/approve with a stale ETag (02 D8).</summary>
    public const string FactSheetConflict = "fact_sheet_conflict";

    /// <summary>Bible PUT with a stale ETag (02 D8).</summary>
    public const string BibleConflict = "bible_conflict";

    /// <summary>Stale <c>se-</c> ETag on series PATCH, or stale <c>sa-</c> ETag on the autopilot settings (09 README D15, 02 D8).</summary>
    public const string SeriesConflict = "series_conflict";

    /// <summary>Stale <c>If-Match</c> on settings (09.10.2).</summary>
    public const string SettingConflict = "setting_conflict";

    /// <summary>Stale <c>pu-</c> ETag on consent/PATCH/cancel (06 CR-9).</summary>
    public const string PublicationConflict = "publication_conflict";

    // ---- 413 ----

    /// <summary>The request body exceeds the public API's byte limit (02.3.2-T1 follow-up, #159).</summary>
    public const string PayloadTooLarge = "payload_too_large";

    // ---- 422, 428, 429 ----

    /// <summary>Same <c>Idempotency-Key</c>, different body (09 README D6).</summary>
    public const string IdempotencyKeyReused = "idempotency_key_reused";

    /// <summary>A D9 route called without <c>If-Match</c> (02 D9).</summary>
    public const string IfMatchRequired = "if_match_required";

    /// <summary>5 story generations / rolling 24 h per episode (02 D8).</summary>
    public const string StoryGenerationLimit = "story_generation_limit";

    /// <summary>More than 30 url sources created by the user in a rolling 24 h (02 D8).</summary>
    public const string SourceFetchLimitReached = "source_fetch_limit_reached";

    /// <summary>YouTube quota exhausted; next slot via <c>GET /publishing/youtube/eta</c> (06).</summary>
    public const string YoutubeQuotaExhausted = "youtube_quota_exhausted";

    /// <summary>TikTok daily creator cap (07.3.3).</summary>
    public const string TiktokCreatorCap = "tiktok_creator_cap";

    /// <summary>TikTok per-token rate limit (07.3.3).</summary>
    public const string TiktokRateLimited = "tiktok_rate_limited";

    /// <summary>Generic rate limit; with <c>Retry-After</c> (09 §5.4).</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>50 failed MFA codes in 24 h: MFA from untrusted devices waits for an e-mailed password reset or 24 h (09.3.2).</summary>
    public const string MfaLockedUntrusted = "mfa_locked_untrusted";

    /// <summary><c>IBudgetGuard</c> stopped the spend (12, 02 D8).</summary>
    public const string SpendCapReached = "spend_cap_reached";

    // ---- 500, 502, 503 ----

    /// <summary>The response of a bug: an unknown code, or a handler that cannot describe its failure. Not a §5.4 row, but the code the mapper writes (09 README D7).</summary>
    public const string InternalError = "internal_error";

    /// <summary><c>POST /admin/settings/{integration}/test</c> reached the host but the harmless call failed (09.10.2).</summary>
    public const string SettingTestFailed = "setting_test_failed";

    /// <summary>The payment provider answered with an error (12 §5.3).</summary>
    public const string PaymentProviderError = "payment_provider_error";

    /// <summary>YouTube upload failed (06).</summary>
    public const string YoutubeUploadFailed = "youtube_upload_failed";

    /// <summary>TikTok upload failed (07).</summary>
    public const string TiktokUploadFailed = "tiktok_upload_failed";

    /// <summary>Siteverify unreachable on a flow that cannot fail open (<c>/auth/register</c>, 09.3.3).</summary>
    public const string CaptchaUnavailable = "captcha_unavailable";

    /// <summary>Transient database failure (09 CR-7).</summary>
    public const string ServiceUnavailable = "service_unavailable";

    // ---- 202 body codes (success values, never ProblemDetails) ----

    /// <summary>202 body code of <c>POST /admin/users/{id}/credits</c> above the 24 h cap, with the <c>credit_grant_requests</c> id (12.5.1).</summary>
    public const string GrantRequiresApproval = "grant_requires_approval";

    /// <summary>202 body code of an owner-on-owner action saved as pending, with the <c>owner_action_requests</c> id (09.10.4).</summary>
    public const string OwnerActionPending = "owner_action_pending";

    /// <summary>202 body code when an owner who is not the target calls cancel on a <c>system</c> request; the objection is audited and alerted, the request stays pending (09.10.4).</summary>
    public const string ObjectionRecorded = "objection_recorded";

    // ---- Job outcomes, job state, provider reasons and redirect values (never an HTTP response) ----

    /// <summary>Render image rejected by moderation; <c>render_jobs.error_code</c> (03 D7/D10).</summary>
    public const string ImageModerated = "image_moderated";

    /// <summary>Image generation failed; <c>render_jobs.error_code</c> (03 D7/D10).</summary>
    public const string ImageGenerationFailed = "image_generation_failed";

    /// <summary><c>render_jobs.error_code</c> after 3 immediate upstream attempts in the audio stage (04 D6).</summary>
    public const string TtsFailed = "tts_failed";

    /// <summary>Output failed the automated quality checks; <c>render_jobs.error_code</c>, no auto-retry (05 D5).</summary>
    public const string RenderQualityFailed = "render_quality_failed";

    /// <summary>Node/adapter error stage; <c>render_jobs.error_code</c> (05 D5).</summary>
    public const string RenderNodeError = "render_node_error";

    /// <summary>Assembly-failure stage; <c>render_jobs.error_code</c> (05 D5).</summary>
    public const string RenderAssemblyFailed = "render_assembly_failed";

    /// <summary>The adapter lost the task; <c>render_jobs.error_code</c>, never re-submitted (05 D5, D15).</summary>
    public const string RenderLost = "render_lost";

    /// <summary>The poll exceeded 60 min; <c>render_jobs.error_code</c> (05 D5, D15).</summary>
    public const string RenderTimeout = "render_timeout";

    /// <summary>Downloaded output failed sha256/size/type verification (09.6.3-T2, 09 W2 CR-12).</summary>
    public const string RenderOutputInvalid = "render_output_invalid";

    /// <summary>The creator cancelled at <c>DELETE /render-jobs/{id}</c>: <c>render_jobs.status = cancelled</c>, never an error response (09.6.4-T1).</summary>
    public const string RenderCancelled = "render_cancelled";

    /// <summary>No node with a free slot: <c>SubmitRenderJob</c> reschedules itself and the job stays <c>queued</c>; Worker-internal (09 W2 CR-11).</summary>
    public const string RenderWorkerUnavailable = "render_worker_unavailable";

    /// <summary>The recipient is deleted or suspended: <c>email_messages.last_error_code</c>, no send and no retry (09.2.6-T3).</summary>
    public const string EmailUserGone = "email_user_gone";

    /// <summary>The SMTP attempt failed transiently: <c>email_messages.last_error_code</c> for the 5-retry policy of the <c>email</c> queue (09.2.6-T3).</summary>
    public const string EmailSendFailed = "email_send_failed";

    /// <summary>
    /// The SMTP server refused the recipient permanently (a 5xx status): <c>email_messages.last_error_code</c>
    /// and the row is failed without a retry, because a second attempt cannot change the answer (09.2.6-T3 L176).
    /// </summary>
    public const string EmailSendRejected = "email_send_rejected";

    /// <summary>The <c>processingPaused</c> reason on episodes and jobs; paid commands still return 202 and hold a reserve (12.8.3).</summary>
    public const string ProviderFundsExhausted = "provider_funds_exhausted";

    /// <summary>TikTok publisher hit the daily active-user cap; <c>publications.error_code</c> (07 D10).</summary>
    public const string TiktokActiveUserCap = "tiktok_active_user_cap";

    /// <summary>The TikTok creator account is banned; <c>publications.error_code</c> (07 D10).</summary>
    public const string TiktokCreatorBanned = "tiktok_creator_banned";

    /// <summary>TikTok rejected the content; <c>publications.error_code</c> (07 D10).</summary>
    public const string TiktokContentRejected = "tiktok_content_rejected";

    /// <summary>The video is invalid for TikTok; <c>publications.error_code</c> (07 D10).</summary>
    public const string TiktokVideoInvalid = "tiktok_video_invalid";

    /// <summary>The TikTok status poll timed out; <c>publications.error_code</c> (07 D10).</summary>
    public const string TiktokPollTimeout = "tiktok_poll_timeout";

    /// <summary>The publication's channel connection must be reconnected; a publication <c>error_code</c> copy key, not an HTTP error (10 CR-29).</summary>
    public const string TiktokReauthRequired = "tiktok_reauth_required";

    /// <summary>A platform incident; <c>publications.error_code</c> (07 D10).</summary>
    public const string PlatformIncident = "platform_incident";

    /// <summary>302-only redirect query value on the TikTok callback; never JSON (07 CR-3).</summary>
    public const string OauthDenied = "oauth_denied";

    /// <summary>302-only redirect query value on the TikTok callback; never JSON (07 CR-3).</summary>
    public const string OauthExchangeFailed = "oauth_exchange_failed";

    /// <summary>302-only redirect query value on the TikTok callback; never JSON (07 CR-3).</summary>
    public const string TiktokScopeMissing = "tiktok_scope_missing";

    /// <summary>Story run outcome: schema-invalid JSON (02 D8).</summary>
    public const string StoryInvalidOutput = "story_invalid_output";

    /// <summary>Story run outcome: provider error or unavailable after the retry budget (02 D8).</summary>
    public const string StoryGenerationFailed = "story_generation_failed";

    /// <summary>Story run outcome: cancelled by system, admin or account deletion (02 D8).</summary>
    public const string StoryGenerationCancelled = "story_generation_cancelled";

    /// <summary>Fact-sheet run outcome: schema-invalid JSON (02 D8).</summary>
    public const string FactSheetInvalidOutput = "fact_sheet_invalid_output";

    /// <summary>Fact-sheet run outcome: the sheet carries a blocking moderation category (02 D8).</summary>
    public const string FactSheetModerated = "fact_sheet_moderated";

    /// <summary>Fact-sheet run outcome: provider error or unavailable after the retry budget (02 D8).</summary>
    public const string FactSheetGenerationFailed = "fact_sheet_generation_failed";

    /// <summary>Series-level run outcome (bible proposal): schema-invalid JSON (02 D8).</summary>
    public const string BibleProposalInvalidOutput = "bible_proposal_invalid_output";

    /// <summary>Series-level run outcome (bible proposal): refusal (02 D8, D12).</summary>
    public const string BibleProposalModerated = "bible_proposal_moderated";

    /// <summary>Series-level run outcome (bible proposal): provider error or unavailable (02 D8).</summary>
    public const string BibleProposalGenerationFailed = "bible_proposal_generation_failed";

    /// <summary>Series-level run outcome (ideas): schema-invalid JSON (02 D8).</summary>
    public const string IdeasInvalidOutput = "ideas_invalid_output";

    /// <summary>Series-level run outcome (ideas): refusal (02 D8, D12).</summary>
    public const string IdeasModerated = "ideas_moderated";

    /// <summary>Series-level run outcome (ideas): provider error or unavailable (02 D8).</summary>
    public const string IdeasGenerationFailed = "ideas_generation_failed";

    /// <summary>The price catalog has no row active at the run's queue time: the <c>OpenAi:Pricing</c> seed has not run (02 T111, 02.2.4-T1).</summary>
    public const string PriceCatalogEmpty = "price_catalog_empty";

    /// <summary>A step of the worst-case estimate has no shipped prompt yet, so the cost bound cannot be computed and the run fails closed (02.2.4-T1, CR-2).</summary>
    public const string PromptNotShipped = "prompt_not_shipped";

    /// <summary>The data object of a review call exceeds twice <c>Story:Prompt:MaxDataObjectBytes</c>, the bound the worst-case cost estimate assumes; the call is not sent (02.6.1-T1 S4).</summary>
    public const string PromptTooLarge = "prompt_too_large";

    /// <summary>Source-run outcome: provider error or unavailable (02 D8).</summary>
    public const string FindSourcesGenerationFailed = "find_sources_generation_failed";

    /// <summary>Source outcome: the host did not answer (02 D8).</summary>
    public const string SourceUnreachable = "source_unreachable";

    /// <summary>Source outcome: the body exceeds the size cap (02 D8).</summary>
    public const string SourceTooLarge = "source_too_large";

    /// <summary>Source outcome: robots.txt forbids the fetch (02 D8).</summary>
    public const string SourceBlockedByRobots = "source_blocked_by_robots";

    /// <summary>Source outcome: unsupported content type (02 D8).</summary>
    public const string SourceUnsupportedType = "source_unsupported_type";

    /// <summary>Source outcome: no extractable text (02 D8).</summary>
    public const string SourceNoText = "source_no_text";

    /// <summary>Internal fetcher API only (02.5.2-T2, T43): the leased task does not exist (it was deleted with its source or episode); never shown to a user.</summary>
    public const string FetchTaskNotFound = "fetch_task_not_found";

    /// <summary>Internal fetcher API only (02.5.2-T2, T43): the caller no longer holds a live lease on the task; never shown to a user.</summary>
    public const string FetchLeaseLost = "fetch_lease_lost";

    /// <summary>Internal fetcher API only (02.5.2-T2): the task kind has no consumer yet (<c>find_sources</c> candidates arrive with 02.7.2-T3); never shown to a user.</summary>
    public const string FetchTaskKindUnsupported = "fetch_task_kind_unsupported";
}
