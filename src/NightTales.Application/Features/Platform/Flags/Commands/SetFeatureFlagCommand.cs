using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Messaging;

namespace NightTales.Application.Features.Platform.Flags.Commands;

/// <summary>
/// <c>nts-worker flags set &lt;key&gt; on|off</c> (09 README BD13, 09.9.2-T2 §Operator CLI): flips one
/// feature flag so a feature can be switched off without a deploy.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AllowAnonymousRequestAttribute"/> is the D4 marker: there is <b>no HTTP endpoint</b> for
/// this command, so no caller carries a principal — the operator boundary is the Worker host and its
/// secret path, and the audit row (<c>data.actor = "system:operator"</c>) is the record. The sibling
/// precedent is 11.3.3-T1's <c>nodes set-state</c> verb in the same host.
/// </para>
/// <para>
/// It is <see cref="NotAuditedAttribute"/> on purpose: the handler writes its own
/// <c>feature_flag_changed</c> row through <c>IAuditWriter</c>, and only on a real transition. Pipeline
/// slot 6 would write one for every success — including a set that changed nothing, which the design
/// explicitly leaves unaudited.
/// </para>
/// </remarks>
/// <param name="Key">The flag key; the handler validates its shape before any lookup.</param>
/// <param name="Enabled">The state to write.</param>
[AllowAnonymousRequest]
[NotAudited("SetFeatureFlagCommandHandler writes its own feature_flag_changed row through IAuditWriter, and only on a real transition; slot 6 would also audit a set that changed nothing.")]
public sealed record SetFeatureFlagCommand(string Key, bool Enabled) : ICommand<ErrorOr<FeatureFlagSetDto>>;

/// <summary>
/// The outcome of one <c>flags set</c> (09.9.2-T2 §Operator CLI): the state the row now has, and whether
/// this call was the one that changed it.
/// </summary>
/// <param name="Key">The flag that was addressed.</param>
/// <param name="Enabled">The state the flag has after the call.</param>
/// <param name="Changed">
/// <see langword="false"/> when the row already had that state — the no-op success that writes neither an
/// audit row nor a notification.
/// </param>
public sealed record FeatureFlagSetDto(string Key, bool Enabled, bool Changed);
