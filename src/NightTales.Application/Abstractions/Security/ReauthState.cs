namespace NightTales.Application.Abstractions.Security;

/// <summary>
/// The durable re-authentication state of one refresh family (09.3.2-T1 Design, BD4; consumed by
/// 09.10.4-T1's re-auth guard).
/// </summary>
/// <remarks>
/// The three members are read from the family's live row, so two Api instances answer the same thing:
/// <c>reauth_attempts</c> is the durable per-family counter WT32 ruled in (never per-instance memory),
/// and <see cref="Revoked"/> is the family's liveness at read time.
/// </remarks>
/// <param name="ReauthenticatedAt">When a re-authentication last succeeded for the family, or null.</param>
/// <param name="ReauthAttempts">Wrong re-authentication attempts charged to the family (0–5, BD4).</param>
/// <param name="Revoked">Whether the family has no live row left.</param>
public sealed record ReauthState(DateTimeOffset? ReauthenticatedAt, int ReauthAttempts, bool Revoked);
