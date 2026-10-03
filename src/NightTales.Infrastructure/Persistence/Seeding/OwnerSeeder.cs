using ErrorOr;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Common.Errors;
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
/// Before <see cref="UserManager{TUser}"/> hashes and stores the password it passes the platform
/// <see cref="IPasswordPolicy"/> — the same BD2 length + breached-password pre-check the register and
/// reset flows run — so the owner can never be seeded with a password those flows would refuse. A
/// rejection and a breached-checker that cannot answer both throw a <see cref="SeedValidationException"/>
/// carrying the error codes only (fail closed, exactly like range mode); the password and the e-mail
/// never reach a message or a log line.
/// </para>
/// <para>
/// Both hosts may seed at once, and their <c>Seed:Owner:Email</c> values may even drift apart. The
/// <c>pg_advisory_xact_lock</c> on <c>seed_owner</c> serialises the existence check and the create in
/// one transaction — the precedent is <c>PriceVersionSeeder</c> — so exactly one host wins and the loser
/// finds the owner committed and reports <see cref="SeedReport.Skipped"/> instead of failing or creating
/// a second owner under another address [DECISION]. An address that already belongs to a normal user is
/// not a lost race — no owner exists, so the seed fails: promotion is an owner action (09.10.4), never
/// a seed side effect. Nothing MFA-related is set here (09.10.4-T1 forces enrolment at the owner's first
/// login).
/// </para>
/// </remarks>
public sealed partial class OwnerSeeder(
    UserManager<AppUser> users,
    AppDbContext db,
    IOptions<SeedOptions> options,
    IPasswordPolicy passwordPolicy,
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

        // The platform password rule runs first and outside the lock: the breached-password lookup may be a
        // network call, and a rejected password must not hold up the other host.
        var verdict = await passwordPolicy.ValidateAsync(owner.Password, ct);
        if (verdict.IsError)
        {
            throw new SeedValidationException(
                $"seed_owner_invalid:{string.Join(',', verdict.Errors.Select(PolicyCode))}");
        }

        var email = owner.Email.Trim();
        var (report, codes) = await CreateLockedAsync(email, owner.Password, ct);
        if (report is not null)
        {
            return report;
        }

        // The transaction is gone, so its lock is released: decide again from what the other host committed.
        SeedDbContext.ResetTracking(db);
        if (await OwnerExistsAsync(ct))
        {
            LogConcurrentOwner();
            return new SeedReport(Name, 0, 0, 1);
        }

        throw new SeedValidationException($"seed_owner_invalid:{string.Join(',', codes)}");
    }

    /// <summary>
    /// The client-safe code of a policy error. A too-short password is <c>validation_failed</c> whose
    /// description is Identity's own code (<c>PasswordTooShort</c>), which is what the seed reports; every
    /// other error (<c>breached_password</c>, <c>service_unavailable</c>) reports its code — never its text.
    /// </summary>
    private static string PolicyCode(Error error)
        => error.Code == ErrorCodes.ValidationFailed ? error.Description : error.Code;

    /// <summary>
    /// Check and create under <c>pg_advisory_xact_lock</c>, so two hosts — even with different configured
    /// e-mails — cannot both see "no owner" and both commit one. Returns the report on success or a skip,
    /// or the refusal codes when nothing was created.
    /// </summary>
    private async Task<(SeedReport? Report, string[] Codes)> CreateLockedAsync(string email, string password, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Released at commit or rollback; the same key serialises the Api, the Worker and nts-seed.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('seed_owner'))", ct);

        if (await OwnerExistsAsync(ct))
        {
            LogConcurrentOwner();
            return (new SeedReport(Name, 0, 0, 1), []);
        }

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
            result = await users.CreateAsync(user, password);
        }
        catch (DbUpdateException exception) when (SeedDbContext.IsUniqueViolation(exception))
        {
            return (null, ["unique_violation"]);
        }

        if (!result.Succeeded)
        {
            return (null, [.. result.Errors.Select(error => error.Code)]);
        }

        await transaction.CommitAsync(ct);
        return (new SeedReport(Name, 1, 0, 0), []);
    }

    private Task<bool> OwnerExistsAsync(CancellationToken ct)
        => db.Users.AsNoTracking().AnyAsync(user => user.Role == PlatformRole.Owner, ct);

    [LoggerMessage(EventId = 2350, Level = LogLevel.Warning, Message = "seed.owner.not-configured owner seed not configured")]
    private partial void LogNotConfigured();

    [LoggerMessage(EventId = 2351, Level = LogLevel.Information, Message = "seed.owner.concurrent-owner")]
    private partial void LogConcurrentOwner();
}
