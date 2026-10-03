using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Domain.Common;

namespace NightTales.Infrastructure.Persistence.Seeding;

/// <summary>
/// Creates the one platform owner from <c>Seed:Owner:Email</c> / <c>Seed:Owner:Password</c> (09.2.5-T2; 09
/// README D9 — <c>users.role</c> is authoritative) when the system has none. It does nothing once an owner
/// exists, so a changed e-mail in the environment never creates a second one, and it never logs or throws
/// the password.
/// </summary>
/// <remarks>
/// <para>
/// The password goes through <see cref="UserManager{TUser}"/>, so it is hashed and policy-checked exactly
/// as a registration is (09.3.1-T1: length ≥ 12, the Identity validators). A refused creation throws a
/// <see cref="SeedValidationException"/> that carries the Identity error codes only — the password and the
/// e-mail never reach a message or a log line.
/// </para>
/// <para>
/// Both hosts may seed at once. The unique <c>normalized_email</c> index lets one win; the loser finds the
/// owner committed and reports <see cref="SeedReport.Skipped"/> instead of failing [DECISION]. An address
/// that already belongs to a normal user is not that case — no owner exists, so the seed fails: promotion
/// is an owner action (09.10.4), never a seed side effect. Nothing MFA-related is set here (09.10.4-T1
/// forces enrolment at the owner's first login).
/// </para>
/// </remarks>
public sealed partial class OwnerSeeder(
    UserManager<AppUser> users,
    AppDbContext db,
    IOptions<SeedOptions> options,
    IClock clock,
    ILogger<OwnerSeeder> logger) : ISeeder
{
    private const string DisplayName = "Owner";

    /// <inheritdoc />
    public string Name => "owner";

    /// <inheritdoc />
    public int Order => 100;

    /// <inheritdoc />
    public async ValueTask<SeedReport> SeedAsync(CancellationToken ct)
    {
        if (await OwnerExistsAsync(ct))
        {
            return new SeedReport(Name, 0, 0, 1);
        }

        var owner = options.Value.Owner;
        if (string.IsNullOrWhiteSpace(owner.Email) || string.IsNullOrEmpty(owner.Password))
        {
            LogNotConfigured();
            return new SeedReport(Name, 0, 0, 0);
        }

        var email = owner.Email.Trim();
        var now = clock.UtcNow;
        var user = new AppUser
        {
            Email = email,
            UserName = email,
            EmailConfirmed = true,
            Role = PlatformRole.Owner,
            Status = UserStatus.Active,
            DisplayName = DisplayName,
            UiLocale = UiLocale.Vi,
            CreatedAt = now,
            UpdatedAt = now,
        };

        IdentityResult result;
        try
        {
            result = await users.CreateAsync(user, owner.Password);
        }
        catch (DbUpdateException exception) when (SeedDbContext.IsUniqueViolation(exception))
        {
            return await LostRaceOrFailAsync(["unique_violation"], ct);
        }

        if (result.Succeeded)
        {
            return new SeedReport(Name, 1, 0, 0);
        }

        return await LostRaceOrFailAsync([.. result.Errors.Select(error => error.Code)], ct);
    }

    private async ValueTask<SeedReport> LostRaceOrFailAsync(string[] codes, CancellationToken ct)
    {
        SeedDbContext.ResetTracking(db);
        if (await OwnerExistsAsync(ct))
        {
            LogConcurrentOwner();
            return new SeedReport(Name, 0, 0, 1);
        }

        throw new SeedValidationException($"seed_owner_invalid:{string.Join(',', codes)}");
    }

    private Task<bool> OwnerExistsAsync(CancellationToken ct)
        => db.Users.AsNoTracking().AnyAsync(user => user.Role == PlatformRole.Owner, ct);

    [LoggerMessage(EventId = 2350, Level = LogLevel.Warning, Message = "seed.owner.not-configured owner seed not configured")]
    private partial void LogNotConfigured();

    [LoggerMessage(EventId = 2351, Level = LogLevel.Information, Message = "seed.owner.concurrent-owner")]
    private partial void LogConcurrentOwner();
}
