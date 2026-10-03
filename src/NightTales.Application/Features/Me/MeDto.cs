using NightTales.Domain.Common;

namespace NightTales.Application.Features.Me;

/// <summary>
/// The body of <c>GET /api/v1/me</c> and <c>PATCH /api/v1/me</c> (09.3.4-T1): the caller's own profile.
/// Deliberately has no <c>createdAt</c> (<c>users</c> has no such column to expose, 09 README G7) and no secret.
/// </summary>
/// <param name="Id">The account id.</param>
/// <param name="DisplayName">The display name.</param>
/// <param name="Email">The sign-in address; read-only.</param>
/// <param name="UiLocale">The UI locale, <c>vi</c> or <c>en</c>.</param>
/// <param name="Role">The platform role; read-only.</param>
/// <param name="MfaEnabled">Whether a second factor is enrolled; read-only.</param>
/// <param name="LastLoginAt">The last successful sign-in.</param>
/// <param name="Features">The enabled keys of <see cref="MeFeatureAllowList"/> (10 CR-10).</param>
public sealed record MeDto(
    Guid Id,
    string DisplayName,
    string Email,
    Language UiLocale,
    PlatformRole Role,
    bool MfaEnabled,
    DateTimeOffset? LastLoginAt,
    IReadOnlyList<string> Features);
