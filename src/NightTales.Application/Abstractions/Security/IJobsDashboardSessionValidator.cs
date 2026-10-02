using NightTales.Domain.Common;

namespace NightTales.Application.Abstractions.Security;

/// <summary>
/// The live-account check of a dashboard session (09.2.6-T2 Design/Failure handling): the cookie's
/// <c>role</c>/<c>stamp</c> claims are re-validated against the account row on every dashboard request,
/// because a demotion, a suspension or a stamp rotation must end the session long before the ticket's
/// 30-minute lifetime would.
/// </summary>
/// <remarks>
/// <para>
/// This is the dashboard's own check, deliberately not 09.3.2-T2's cached stamp validation: that task is
/// not landed, and the dashboard is a low-traffic operator surface, so one bounded, no-tracking read of
/// the account's <c>role</c>, <c>status</c> and <c>security_stamp</c> per request is the whole cost.
/// </para>
/// <para>
/// It fails closed. An account that no longer exists, is not <see cref="UserStatus.Active"/>, no longer
/// holds the ticket's role, or — when the ticket carries a <c>stamp</c> — no longer carries that stamp is
/// answered <see langword="false"/>.
/// </para>
/// </remarks>
public interface IJobsDashboardSessionValidator
{
    /// <summary>
    /// Whether the account behind a dashboard ticket still matches the ticket's claims.
    /// </summary>
    /// <param name="userId">The ticket's <c>sub</c>: the account to read.</param>
    /// <param name="role">The ticket's <c>role</c>: an <c>owner</c> or an <c>admin</c>.</param>
    /// <param name="securityStamp">
    /// The ticket's <c>stamp</c>, or <see langword="null"/> when it carries none (the check then compares
    /// only the account's existence, status and role).
    /// </param>
    /// <param name="ct">The request cancellation token.</param>
    /// <returns><see langword="true"/> only when the account is live and still matches the ticket.</returns>
    Task<bool> IsStillValidAsync(Guid userId, PlatformRole role, string? securityStamp, CancellationToken ct);
}
