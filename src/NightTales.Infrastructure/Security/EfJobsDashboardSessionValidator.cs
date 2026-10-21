using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Security;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence;

namespace NightTales.Infrastructure.Security;

/// <summary>
/// The live-account check of a dashboard session (09.2.6-T2 Failure handling): one no-tracking,
/// primary-key read of the ticket's <c>users</c> row, compared with the cookie's <c>role</c> and
/// <c>stamp</c> claims.
/// </summary>
/// <remarks>
/// It reads the write model's <c>Users</c> set directly, the way the other 09 security flows do
/// (<c>RefreshSessionService</c>, <c>EfMfaEnrolmentService</c>): the dashboard must see the authoritative
/// account row, and the account table is not tenant-scoped, so no <c>ISystemContext.For</c> scope is
/// needed for the read. The projection keeps it to the three columns the comparison needs.
/// </remarks>
/// <param name="database">The scope's write model.</param>
public sealed class EfJobsDashboardSessionValidator(AppDbContext database) : IJobsDashboardSessionValidator
{
    /// <inheritdoc />
    public async Task<bool> IsStillValidAsync(
        Guid userId,
        PlatformRole role,
        string? securityStamp,
        CancellationToken ct)
    {
        var account = await database.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new { user.Role, user.Status, user.SecurityStamp })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return account is { Status: UserStatus.Active }
            && account.Role == role
            && (securityStamp is null
                || string.Equals(account.SecurityStamp, securityStamp, StringComparison.Ordinal));
    }
}
