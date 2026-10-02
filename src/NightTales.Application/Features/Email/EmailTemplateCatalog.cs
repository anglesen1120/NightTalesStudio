using NightTales.Domain.Common;

namespace NightTales.Application.Features.Email;

/// <summary>
/// One template of the platform's e-mail catalog (09 README BD9, 09.2.6-T3): the two closed key sets the
/// template may be rendered with, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ProducerKeys"/> is what an <c>EmailMessage</c> of this template may carry — the producer's
/// values only, so <c>QueueAsync</c> answers <c>email_template_keys</c> for anything else.
/// <see cref="MinterKeys"/> is what a token-bearing template gets at send time from its
/// <c>IEmailTokenMinter</c>: those values exist only inside <c>SendEmailJob</c> and are never queued,
/// persisted or logged (09 README §B10 WT17, WT30). The two sets are disjoint by construction, so a
/// producer can never smuggle a send-time value into the queue.
/// </para>
/// <para>
/// The sets are code and the keys are the placeholders of the shipped <c>.txt</c>/<c>.html</c> files, so
/// the composition-time validation of <c>EmailTemplateProvider</c> can prove that no file uses a
/// placeholder this catalog does not declare — the catalog cannot drift from the text.
/// </para>
/// </remarks>
public sealed record EmailTemplateDefinition
{
    /// <summary>Declares one template's two key sets.</summary>
    /// <param name="producerKeys">The keys an <c>EmailMessage</c> of this template may carry; never empty in practice.</param>
    /// <param name="minterKeys">The keys the template's minter produces at send time; empty for every template without one.</param>
    /// <exception cref="InvalidOperationException">A key set is empty, or a key is in both sets.</exception>
    public EmailTemplateDefinition(
        IReadOnlyCollection<string> producerKeys,
        IReadOnlyCollection<string>? minterKeys = null)
    {
        ArgumentNullException.ThrowIfNull(producerKeys);

        ProducerKeys = new HashSet<string>(producerKeys, StringComparer.Ordinal);
        MinterKeys = minterKeys is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(minterKeys, StringComparer.Ordinal);

        if (ProducerKeys.Count == 0 && MinterKeys.Count == 0)
        {
            throw new InvalidOperationException(
                "An e-mail template must declare at least one render key; an empty template is a catalog bug.");
        }

        // A key is either supplied by the producer or minted at send time; overlapping sets would make the
        // WT30 rule ("a token never travels through `params`") unenforceable at the catalog level.
        if (ProducerKeys.Overlaps(MinterKeys))
        {
            throw new InvalidOperationException(
                "A template key cannot be both producer-supplied and minted at send time (09 README §B10 WT30).");
        }

        var renderKeys = new HashSet<string>(ProducerKeys, StringComparer.Ordinal);
        renderKeys.UnionWith(MinterKeys);
        RenderKeys = renderKeys;
    }

    /// <summary>The keys an <c>EmailMessage</c> of this template may carry.</summary>
    public IReadOnlySet<string> ProducerKeys { get; }

    /// <summary>The keys the template's minter produces at send time.</summary>
    public IReadOnlySet<string> MinterKeys { get; }

    /// <summary>Every key a shipped template file may use as a <c>{{key}}</c> placeholder.</summary>
    public IReadOnlySet<string> RenderKeys { get; }
}

/// <summary>
/// The closed catalog of e-mail templates (09 README BD9, 09.2.6-T3): name → its two key sets. One
/// template is a pair of files per locale,
/// <c>Templates/Email/&lt;name&gt;.&lt;locale&gt;.{txt,html}</c> (shipped as <c>Content</c> beside the
/// assembly, CR-13).
/// </summary>
/// <remarks>
/// <para>
/// Every name is owned by the task that queues it, listed in BD1's ownership map; a name is added here in
/// the same commit as its two file pairs, because the composition-time validation of
/// <c>EmailTemplateProvider</c> refuses a catalog entry without files and a file without a catalog entry.
/// </para>
/// <para>
/// Every link that carries a single-use token — the reset/verify <c>link</c> and the owner-action
/// <c>approveUrl</c>/<c>cancelUrl</c>/<c>resetUrl</c>/<c>cancelLink</c> — is declared as a <i>minter</i> key
/// and deliberately not as a producer key: it is minted inside <c>SendEmailJob</c> from a single-use row
/// created there, so it never travels through <c>params</c> or an outbox payload (09 README §B10 WT17,
/// WT30). A template whose minter is not registered yet (the S-phase <c>email_verify</c>, the wave-3 owner
/// templates) fails the one row that reaches it and never breaks the rest of the pipeline.
/// </para>
/// </remarks>
public static class EmailTemplateCatalog
{
    /// <summary>The template of 09.3.3-T1's password reset; its link is minted at send time.</summary>
    public const string PasswordReset = "password_reset";

    /// <summary>The reset-success notice of 09.3.3-T1 (09 §4 09.3.3 line 2): token-free.</summary>
    public const string PasswordChanged = "password_changed";

    /// <summary>The template of 09.3.1-T3's e-mail verification (S phase); its link is minted at send time.</summary>
    public const string EmailVerify = "email_verify";

    /// <summary>Every template the platform ships, keyed by its exact name.</summary>
    public static readonly IReadOnlyDictionary<string, EmailTemplateDefinition> All =
        new Dictionary<string, EmailTemplateDefinition>(StringComparer.Ordinal)
        {
            // 09.3.3-T1. `link` is minted at send time, never an allow-listed param (WT17/WT30).
            [PasswordReset] = new(["displayName", "expiresMinutes"], ["link"]),

            // 09.3.1-T3 (S). `link` is minted at send time (WT17).
            [EmailVerify] = new(["displayName", "expiresHours"], ["link"]),

            // BD3: a reused refresh token revoked the family; the notice names where to look (no token).
            ["refresh_token_reuse"] = new(["displayName", "occurredAt", "device", "sessionsLink"]),

            // 09.3.3-T1's reset-success notice (09 §4 09.3.3 line 2).
            ["password_changed"] = new(["displayName", "occurredAt"]),

            // 09.3.1-T3: "someone tried to register with your e-mail" (09 §4 09.3.1 line 2).
            ["email_verify_duplicate"] = new(["displayName", "occurredAt"]),

            // 09.3.3-T3 (09 W3 CR-8).
            ["mfa_failed_notice"] = new(["displayName", "failedCount", "windowMinutes", "securityUrl"]),

            // 09.3.7-T1 (09 W3 CR-8).
            ["session_new_device"] = new(["displayName", "device", "ip", "at", "securityUrl"]),

            // 09.10.4-T3, pre-change configuration snapshot (09 W3 CR-8).
            ["security_alert"] = new(["event", "severity", "at", "actorEmail", "dataSummary"]),

            // 09.10.4-T2 (09 W3 CR-8). `approveUrl`/`cancelUrl` are single-use owner-action links: they are
            // minted at send time by 09.10.4's own keyed minter and never queued (09 README §B10 WT17/WT30).
            ["owner_action_pending"] = new(["action", "targetDisplayName", "reason", "applyAfter"], ["approveUrl", "cancelUrl"]),

            // 09.10.2-T2 (09 W3 CR-8). The same pair of send-time links as `owner_action_pending`.
            ["setting_change_pending"] = new(["integration", "changedKeys", "applyAfter"], ["approveUrl", "cancelUrl"]),

            // 09.10.5-T1 mints `resetUrl` at send time, reusing 09.3.3-T1's password-reset minter (09 W3 CR-8).
            ["mfa_reset_forced"] = new(["displayName"], ["resetUrl"]),

            // 09.10.8-T1; `requestId` is the cancel page's own reference for its log line (09 W3 CR-8).
            // `cancelLink` carries the subject-scoped single-use token of 06.2.2-T2 and is minted at send
            // time, so it is a minter key and never travels through `params` (09 README §B10 WT30).
            ["approval_cancel"] = new(["displayName", "requestKind", "reason", "deadline", "requestId"], ["cancelLink"]),

            // 02.8.3-T4.
            ["autopilot_alert"] = new(["seriesTitle", "reasonLine", "scheduledList", "deepLink", "resumeHint"]),

            // 02.8.2-T1.
            ["autopilot_owner_action_required"] = new(["seriesTitle", "episodeTitle", "slot", "failedChecks", "deepLink"]),
        };

    /// <summary>The definition of <paramref name="template"/>, or <see langword="null"/> when it is unknown.</summary>
    /// <param name="template">The template name an <c>EmailMessage</c> carries.</param>
    public static EmailTemplateDefinition? Find(string template) =>
        All.TryGetValue(template, out var definition) ? definition : null;

    /// <summary>
    /// The file-name form of <paramref name="locale"/> (09 README BD9: <c>&lt;name&gt;.&lt;locale&gt;.txt</c>),
    /// so the template file pattern is spelled once.
    /// </summary>
    /// <param name="locale">The render locale; <see cref="Language"/> is the only locale enum (D16).</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "The file-name suffix is the lower-case locale name `vi`/`en` that 09 README BD9 fixes; upper-casing it would change the contract.")]
    public static string LocaleName(Language locale) =>
        Enum.GetName(locale)?.ToLowerInvariant()
        ?? throw new ArgumentOutOfRangeException(nameof(locale), locale, "Unknown e-mail locale.");
}
