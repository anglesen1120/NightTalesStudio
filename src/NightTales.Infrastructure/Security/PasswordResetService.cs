using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ErrorOr;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Email;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Email;
using NightTales.Domain.Audit;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// The password-reset flows of 09.3.3-T1 (09 README BD3/BD16, WF11/WF12): queue the reset link and spend
/// it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layer ruling.</b> This lives in Infrastructure because it reads and writes <c>AppUser</c>, changes
/// the password through <c>UserManager</c> and revokes the refresh families through
/// <c>IRefreshTokenService</c>; the Application handler only runs the password policy and delegates
/// (<c>IPasswordResetService</c> is the seam), exactly as 09.3.2-T1's login flow does.
/// </para>
/// <para>
/// <b>No enumeration (BD16).</b> A request answers success whatever happens: an unknown address, an
/// inactive account and a live one are indistinguishable in the response. The difference is operational
/// only — a queued message, an audit row, a counter — and even a queue failure is swallowed for the
/// response but logged and counted so it is never silent.
/// </para>
/// <para>
/// <b>One transaction (D5).</b> Every write — the token consume, the password change, the stamp rotation,
/// the family revocation, the audit rows and the queued notice — joins the handler's unit of work, so a
/// refusal leaves nothing behind. The account's lifecycle lock (<see cref="SecuritySql.LockUserAsync"/>)
/// is taken before the user is loaded, so a reset and an MFA change serialize on the account and both
/// take the family locks only after it. The e-mail token itself is <em>not</em> issued here: the request
/// queues ids and token-free values, and <c>PasswordResetTokenMinter</c> mints the row inside
/// <c>SendEmailJob</c> (WT17/WT30).
/// </para>
/// </remarks>
/// <param name="database">The scope's write model.</param>
/// <param name="readDb">The no-tracking read model, used for the request's e-mail lookup.</param>
/// <param name="users">The Identity manager that changes the password.</param>
/// <param name="lookupNormalizer">Identity's own e-mail normalization, so the lookup matches registration's.</param>
/// <param name="actionTokens">The single-use token store.</param>
/// <param name="refreshTokens">The family revocation of 09.3.2-T1 (WF12).</param>
/// <param name="securityEvents">The stamp rotation of 09.3.2-T2/D9.</param>
/// <param name="email">The queueing port; it never sends inline.</param>
/// <param name="hasher">The peppered address hash the request's audit row carries.</param>
/// <param name="audit">The <c>account_security</c> writer.</param>
/// <param name="alerter">The operator alert for a privileged account's reset.</param>
/// <param name="tokenOptions">The reset lifetime the queued message advertises.</param>
/// <param name="metrics">The action-token and password-reset counters.</param>
/// <param name="clock">The scope's clock.</param>
/// <param name="logger">The operational log; it carries the template and a user-id hash only.</param>
public sealed partial class PasswordResetService(
    AppDbContext database,
    IReadDb readDb,
    UserManager<AppUser> users,
    ILookupNormalizer lookupNormalizer,
    IUserActionTokenService actionTokens,
    IRefreshTokenService refreshTokens,
    ISecurityEventWriter securityEvents,
    IEmailSender emailSender,
    IEmailAddressHasher hasher,
    IAuditWriter audit,
    ISecurityAlerter alerter,
    IOptions<UserActionTokenOptions> tokenOptions,
    AuthMetrics metrics,
    IClock clock,
    ILogger<PasswordResetService> logger) : IPasswordResetService
{
    /// <summary>The template name the request queues; also the metric's <c>kind</c> value.</summary>
    private const string ResetTemplate = EmailTemplateCatalog.PasswordReset;

    /// <summary>The wire value of <c>UserActionKind.PasswordReset</c>, used for the metric's <c>kind</c> tag.</summary>
    private const string ResetKind = "password_reset";

    /// <summary>The reason stored on the revocation and the stamp rotation.</summary>
    private const string ResetReason = "password_reset";

    /// <summary>The web serializer defaults, so a hand-written <c>data</c> payload's keys are camelCase.</summary>
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<ErrorOr<Success>> RequestResetAsync(
        string email,
        RequestInfo info,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);

        var now = clock.UtcNow;
        var normalized = lookupNormalizer.NormalizeEmail(email);

        var user = await readDb
            .Query<AppUser>()
            .FirstOrDefaultAsync(candidate => candidate.NormalizedEmail == normalized, cancellationToken)
            .ConfigureAwait(false);

        if (user is null || user.Status != UserStatus.Active)
        {
            // The same amount of work as the live path (a hash over 32 random bytes) and no write, no
            // mail, no audit row: nothing an observer of the response or the clock could tell apart.
            SpendIssueWork();
            metrics.ActionToken(ResetKind, "skipped");

            return Result.Success;
        }

        var queued = await emailSender
            .QueueAsync(
                new EmailMessage(
                    user.Id,
                    ResetTemplate,
                    ToLanguage(user.UiLocale),
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["displayName"] = user.DisplayName,
                        ["expiresMinutes"] = tokenOptions.Value.PasswordResetMinutes
                            .ToString(System.Globalization.CultureInfo.InvariantCulture),
                    }),
                cancellationToken)
            .ConfigureAwait(false);

        if (queued.IsError)
        {
            // Swallowed for the response — an error would betray that the account exists — but never
            // silent: the template and a hash of the user id are logged, and the counter moves.
            LogEnqueueFailed(ResetTemplate, UserHash(user.Id));
            metrics.ActionToken(ResetKind, "enqueue_failed");

            return Result.Success;
        }

        // The request's own row: the account exists and is active, so the row is user-visible and carries
        // only the peppered address hash — never the address (BD15/BD16, C3.4).
        await audit
            .WriteAsync(
                SecurityAudit.EntryWithData(
                    SecurityAuditActions.PasswordResetRequested,
                    user.Id,
                    info,
                    Visibility.User,
                    JsonSerializer.Serialize(new ResetRequestData(hasher.Hash(email)), PayloadOptions),
                    now),
                cancellationToken)
            .ConfigureAwait(false);

        metrics.ActionToken(ResetKind, "queued");

        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<ErrorOr<Success>> ResetAsync(
        string token,
        string newPassword,
        RequestInfo info,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);

        var now = clock.UtcNow;

        // 1. Spend the token first: the single statement serializes two concurrent resets, and only a
        //    genuinely bad token (used, expired, unknown, wrong kind) is the 400 of 09 §4. A transient
        //    store failure is the 503 ConsumeAsync itself mapped and must not be disguised as a bad link.
        var consumed = await actionTokens
            .ConsumeAsync(token, UserActionKind.PasswordReset, cancellationToken)
            .ConfigureAwait(false);

        if (consumed.IsError)
        {
            metrics.PasswordReset("failed");

            return consumed.Errors;
        }

        // 2. The account's lifecycle lock, before the user is loaded or changed. The MFA lifecycle takes
        //    the same lock first (EfMfaEnrolmentService.DisableAsync), so a reset and an MFA change
        //    serialize on the account and both take family locks only after it — one lock order, no
        //    inversion (09.3.2-T2/D9, 09.10.4-T1).
        await SecuritySql.LockUserAsync(database, consumed.Value, cancellationToken).ConfigureAwait(false);

        // 3. The account must still be active; a suspended one answers the same 400, never "suspended".
        var user = await database.Users
            .FirstOrDefaultAsync(candidate => candidate.Id == consumed.Value, cancellationToken)
            .ConfigureAwait(false);

        if (user is null || user.Status != UserStatus.Active)
        {
            metrics.PasswordReset("failed");

            return InvalidToken();
        }

        // 4. Identity's own change: remove then add, so the account ends with exactly the new hash. Any
        //    refusal is the same 400 with a safe description — never the password.
        var removed = await users.RemovePasswordAsync(user).ConfigureAwait(false);
        if (!removed.Succeeded)
        {
            metrics.PasswordReset("failed");

            return PasswordRefused(removed);
        }

        var added = await users.AddPasswordAsync(user, newPassword).ConfigureAwait(false);
        if (!added.Succeeded)
        {
            metrics.PasswordReset("failed");

            return PasswordRefused(added);
        }

        // 5. Rotate the stamp (09.3.2-T2/D9, W3 CR-20) and revoke every family of the account: the reset
        //    is exactly the "everything the old password unlocked dies" case (WF12, 09 §4).
        var rotated = await securityEvents
            .RotateStampAsync(user.Id, ResetReason, cancellationToken)
            .ConfigureAwait(false);

        if (rotated.IsError)
        {
            metrics.PasswordReset("failed");

            return rotated.Errors;
        }

        // 6. A refused revocation must fail the whole command: the password, the stamp and the spent
        //    token are already written in this transaction, and a reset that left a live session behind
        //    is exactly the failure mode WF12 forbids. Returning the error rolls all of it back.
        var revoked = await refreshTokens
            .RevokeAllFamiliesAsync(user.Id, ResetReason, exceptFamilyId: null, cancellationToken)
            .ConfigureAwait(false);

        if (revoked.IsError)
        {
            metrics.PasswordReset("failed");

            return revoked.Errors;
        }

        // 7. The user-visible account_security row, then the operator alert for a privileged account.
        await audit
            .WriteAsync(
                SecurityAudit.Entry(
                    SecurityAuditActions.PasswordReset,
                    user.Id,
                    info,
                    Visibility.User,
                    detail: null,
                    now),
                cancellationToken)
            .ConfigureAwait(false);

        if (user.Role is PlatformRole.Admin or PlatformRole.Owner)
        {
            await alerter
                .AlertAsync(
                    new SecurityEvent(
                        SecurityEventKinds.PasswordReset,
                        user.Id,
                        "A privileged account reset its password through the mailed link."),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // 8. The "your password changed" notice. A queue failure must not roll the reset back — the
        //    password is already changed — so it is logged and counted, never returned.
        var notice = await emailSender
            .QueueAsync(
                new EmailMessage(
                    user.Id,
                    EmailTemplateCatalog.PasswordChanged,
                    ToLanguage(user.UiLocale),
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["displayName"] = user.DisplayName,
                        ["occurredAt"] = now.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    }),
                cancellationToken)
            .ConfigureAwait(false);

        if (notice.IsError)
        {
            LogEnqueueFailed(EmailTemplateCatalog.PasswordChanged, UserHash(user.Id));
            metrics.PasswordReset("notice_failed");

            return Result.Success;
        }

        metrics.PasswordReset("ok");

        return Result.Success;
    }

    /// <summary>The 400 every failed reset shares, so a caller cannot tell its causes apart (09 §4).</summary>
    private static Error InvalidToken() =>
        Error.Validation(
            code: ErrorCodes.ValidationFailed,
            description: "The link is invalid, already used or expired.",
            metadata: new Dictionary<string, object> { [ErrorMetadata.Field] = "token" });

    /// <summary>
    /// The 400 of an Identity refusal: the descriptions are Identity's own, which never echo the password.
    /// </summary>
    private static Error PasswordRefused(IdentityResult result) =>
        Errors.ValidationFailed(
            "newPassword",
            string.Join(" ", result.Errors.Select(error => error.Description)));

    /// <summary>The dummy work of the not-found path: a hash over 32 random bytes, like a mint would do.</summary>
    private static void SpendIssueWork() =>
        _ = Convert.ToHexStringLower(SHA256.HashData(RandomNumberGenerator.GetBytes(32)));

    /// <summary>A short correlation hash of a user id, so a log line names no account (BD16).</summary>
    private static string UserHash(Guid userId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(userId.ToString())))[..16];

    /// <summary>The one locale enum (D16): the account's <c>ui_locale</c> is the message's locale.</summary>
    private static Language ToLanguage(UiLocale locale) => locale is UiLocale.En ? Language.En : Language.Vi;

    [LoggerMessage(
        EventId = 3310,
        Level = LogLevel.Error,
        Message = "password-reset enqueue failed: template={Template} userHash={UserHash}")]
    private partial void LogEnqueueFailed(string template, string userHash);

    /// <summary>The <c>data</c> payload of the request's row: the peppered address hash, nothing else.</summary>
    /// <param name="EmailHash">The hex SHA-256 of the address with the platform pepper.</param>
    private sealed record ResetRequestData(string EmailHash);
}
