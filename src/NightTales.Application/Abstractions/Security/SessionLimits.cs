using NightTales.Domain.Common;

namespace NightTales.Application.Abstractions.Security;

/// <summary>
/// How long a refresh family may live (09 README BD3): an absolute ceiling every role has, plus an idle
/// timeout only the owner and admin roles have.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="For"/> holds the BD3 values a host must not widen; <c>Auth:Session:*</c> mirrors them and
/// the start-up validation of <c>SessionOptions</c> refuses a configuration beyond the owner/admin
/// bounds (09.3.2-T1 Design). The record is the shared vocabulary of that check, of the refresh store and
/// of 09.10.4-T1's role/session-limit rule.
/// </para>
/// <para>
/// A user family has no idle timeout: <see cref="Idle"/> is null, and only <see cref="Absolute"/>
/// applies (BD3 — 14 days).
/// </para>
/// </remarks>
/// <param name="Absolute">The ceiling measured from the family's first row, which a rotation can never extend.</param>
/// <param name="Idle">The maximum silence between two uses, or null when the role has no idle timeout.</param>
public sealed record SessionLimits(TimeSpan Absolute, TimeSpan? Idle)
{
    /// <summary>The BD3 limits of <paramref name="role"/>.</summary>
    /// <param name="role">The role the family was opened for.</param>
    public static SessionLimits For(PlatformRole role) => role switch
    {
        PlatformRole.Owner or PlatformRole.Admin => new(TimeSpan.FromHours(12), TimeSpan.FromMinutes(30)),
        _ => new(TimeSpan.FromDays(14), null),
    };
}
