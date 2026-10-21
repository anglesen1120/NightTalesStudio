using NightTales.Domain.Common;

namespace NightTales.Api.Realtime;

/// <summary>
/// The hub's connection policy, bound from the <c>Hubs</c> section (09.8.1 plan §7;
/// [RULED — README §B10 WT37]).
/// </summary>
/// <remarks>
/// <para>
/// <c>Hubs:MaxConnectionMinutes</c> is the <em>absolute</em> cap of a socket and equals the
/// <em>role's</em> access-token lifetime: owner and admin 5 minutes, user 15 (WT37's second ruling,
/// which replaced the former single <c>Realtime:MaxConnectionMinutes</c>). Nothing on the hub path
/// reads a stamp, a status or a cache to shorten it — a revoked user's socket is closed by the
/// <c>security</c> NOTIFY instead (09-backend-api L218), and this cap is the backstop that bounds the
/// rest.
/// </para>
/// <para>
/// The map is keyed by the role's name exactly as <c>users.role</c> stores it (snake_case, 09 README
/// D8/D16); a role the map does not name falls back to its ruled default, so a deployment that tunes
/// only one role still starts.
/// </para>
/// </remarks>
public sealed class HubsOptions
{
    /// <summary>The configuration section this type binds to.</summary>
    public const string SectionName = "Hubs";

    /// <summary>The ruled cap of <see cref="PlatformRole.Owner"/> in minutes when the map is silent.</summary>
    public const int DefaultOwnerMinutes = 5;

    /// <summary>The ruled cap of <see cref="PlatformRole.Admin"/> in minutes when the map is silent.</summary>
    public const int DefaultAdminMinutes = 5;

    /// <summary>The ruled cap of <see cref="PlatformRole.User"/> in minutes when the map is silent.</summary>
    public const int DefaultUserMinutes = 15;

    /// <summary>
    /// The caps in minutes, keyed by role name (<c>Hubs:MaxConnectionMinutes:owner</c>,
    /// <c>:admin</c>, <c>:user</c>); a role that is absent uses its ruled default (owner/admin 5,
    /// user 15 — WT37).
    /// </summary>
    public Dictionary<string, int> MaxConnectionMinutes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The cap of <paramref name="role"/> as a duration, measured from the connection's claim.</summary>
    /// <param name="role">The role the ticket's principal carries.</param>
    public TimeSpan CapFor(PlatformRole role) => TimeSpan.FromMinutes(MinutesFor(role));

    /// <summary>The cap of <paramref name="role"/> in minutes.</summary>
    /// <param name="role">The role the ticket's principal carries.</param>
    public int MinutesFor(PlatformRole role) =>
        MaxConnectionMinutes.TryGetValue(RoleName(role), out var minutes) && minutes > 0
            ? minutes
            : DefaultMinutes(role);

    /// <summary>The map key of <paramref name="role"/>: its snake_case name (09 README D8/D16).</summary>
    /// <param name="role">The platform role.</param>
    public static string RoleName(PlatformRole role) => role.ToString().ToLowerInvariant();

    /// <summary>The ruled default of <paramref name="role"/> in minutes (WT37).</summary>
    /// <param name="role">The platform role.</param>
    public static int DefaultMinutes(PlatformRole role) => role == PlatformRole.User
        ? DefaultUserMinutes
        : DefaultOwnerMinutes;

    /// <summary>
    /// The keys of <see cref="MaxConnectionMinutes"/> that are not a <see cref="PlatformRole"/> member:
    /// a typo (<c>ownr</c>) would otherwise be a cap that is never applied, so start-up validation
    /// fails on it instead.
    /// </summary>
    internal IReadOnlyList<string> UnknownRoleNames() =>
        [.. MaxConnectionMinutes.Keys.Where(key => !Enum.GetNames<PlatformRole>().Contains(key, StringComparer.OrdinalIgnoreCase))];
}
