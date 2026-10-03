using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence;

namespace NightTales.Infrastructure.Identity;

/// <summary>
/// The profile read of <c>GET /me</c> (09.3.4-T1): one SQL projection over <c>users</c> by the caller's id,
/// no tracking, no entity materialisation. <see cref="AppUser"/> is not tenant-filtered (it is the tenant),
/// so the id predicate here is the isolation: there is no way to ask for another account.
/// </summary>
/// <param name="readDb">The no-tracking read model.</param>
public sealed class UserProfileReader(IReadDb readDb) : IUserProfileReader
{
    /// <inheritdoc />
    public async Task<UserProfile?> GetAsync(Guid userId, CancellationToken ct)
    {
        var row = await readDb.Query<AppUser>()
            .Where(user => user.Id == userId && user.Status != UserStatus.Deleted)
            .Select(user => new
            {
                user.Id,
                user.DisplayName,
                Email = user.Email ?? string.Empty,
                user.UiLocale,
                user.Role,
                user.TwoFactorEnabled,
                user.LastLoginAt,
            })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return row is null
            ? null
            : new UserProfile(
                row.Id,
                row.DisplayName,
                row.Email,
                UserProfileMapping.ToLanguage(row.UiLocale),
                row.Role,
                row.TwoFactorEnabled,
                row.LastLoginAt);
    }
}

/// <summary>
/// The profile write of <c>PATCH /me</c> (09.3.4-T1): loads the caller's tracked row and sets the two writable
/// members. It never calls <c>SaveChangesAsync</c> — <c>UnitOfWorkBehavior</c> commits it with the audit row
/// (09 README D5) — and only a row the id names is ever touched.
/// </summary>
/// <param name="context">The scope's write model (the caller's unit of work).</param>
public sealed class UserProfileWriter(AppDbContext context) : IUserProfileWriter
{
    /// <inheritdoc />
    public async Task<UserProfileUpdate?> UpdateAsync(
        Guid userId,
        string? displayName,
        Language? uiLocale,
        CancellationToken ct)
    {
        var user = await context.Users
            .FirstOrDefaultAsync(candidate => candidate.Id == userId && candidate.Status != UserStatus.Deleted, ct)
            .ConfigureAwait(false);

        if (user is null)
        {
            return null;
        }

        var previousLocale = UserProfileMapping.ToLanguage(user.UiLocale);
        var nameChanged = displayName is not null && !string.Equals(user.DisplayName, displayName, StringComparison.Ordinal);

        if (displayName is not null)
        {
            user.DisplayName = displayName;
        }

        if (uiLocale is { } locale)
        {
            user.UiLocale = UserProfileMapping.ToUiLocale(locale);
        }

        return new UserProfileUpdate(
            new UserProfile(
                user.Id,
                user.DisplayName,
                user.Email ?? string.Empty,
                UserProfileMapping.ToLanguage(user.UiLocale),
                user.Role,
                user.TwoFactorEnabled,
                user.LastLoginAt),
            previousLocale,
            nameChanged);
    }
}

internal static class UserProfileMapping
{
    internal static Language ToLanguage(UiLocale locale) => locale switch
    {
        UiLocale.Vi => Language.Vi,
        UiLocale.En => Language.En,
        _ => throw new ArgumentOutOfRangeException(nameof(locale), locale, "Unknown UI locale."),
    };

    internal static UiLocale ToUiLocale(Language language) => language switch
    {
        Language.Vi => UiLocale.Vi,
        Language.En => UiLocale.En,
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, "Unknown language."),
    };
}
