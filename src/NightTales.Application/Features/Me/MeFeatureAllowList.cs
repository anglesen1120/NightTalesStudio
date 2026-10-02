using NightTales.Application.Abstractions.Flags;
using NightTales.Application.Abstractions.Identity;

namespace NightTales.Application.Features.Me;

/// <summary>
/// The feature keys <c>/me</c> may expose (10 CR-10). Only these keys are ever asked for, so the endpoint
/// cannot be used to enumerate the platform's flags.
/// </summary>
public static class MeFeatureAllowList
{
    /// <summary>The allow-listed keys, in the order the response lists them.</summary>
    public static readonly IReadOnlyList<string> Keys = ["source_finder", "youtube_publish"];

    /// <summary>The enabled allow-listed keys, in allow-list order.</summary>
    /// <param name="flags">The read side of the feature flags (fail-closed, cached).</param>
    /// <param name="ct">The caller's cancellation token.</param>
    internal static async Task<IReadOnlyList<string>> EnabledAsync(IFeatureFlags flags, CancellationToken ct)
    {
        var enabled = new List<string>(Keys.Count);

        foreach (var key in Keys)
        {
            if (await flags.IsEnabledAsync(key, ct).ConfigureAwait(false))
            {
                enabled.Add(key);
            }
        }

        return enabled;
    }

    internal static MeDto ToDto(UserProfile profile, IReadOnlyList<string> features) => new(
        profile.Id,
        profile.DisplayName,
        profile.Email,
        profile.UiLocale,
        profile.Role,
        profile.MfaEnabled,
        profile.LastLoginAt,
        features);
}
