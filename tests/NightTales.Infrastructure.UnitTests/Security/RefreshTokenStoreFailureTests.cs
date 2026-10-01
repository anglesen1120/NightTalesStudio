using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Entities;
using NightTales.Infrastructure.Persistence.UnitOfWork;
using NightTales.Infrastructure.Security;
using NightTales.Infrastructure.UnitTests.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Security;

/// <summary>
/// A transient database failure inside the refresh-token store comes back as a typed 503, never as an
/// unhandled 500 (09.3.2-T1 CR-2, 09 README D5). The failure is induced the way it happens in production: a
/// deadlock between two transactions that take the same two locks in opposite orders, which PostgreSQL ends
/// with SQLSTATE 40P01 — a failure <c>NpgsqlException.IsTransient</c> reports.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RefreshTokenStoreFailureTests(PostgresTestFixture fixture) : IClassFixture<PostgresTestFixture>
{
    [Fact]
    public async Task RevokeFamily_WhenTheStatementDeadlocks_ReturnsServiceUnavailable()
    {
        var ct = TestContext.Current.CancellationToken;

        using var first = fixture.CreateAppDbContext();
        using var second = fixture.CreateAppDbContext();

        var clock = new FakeClock();
        var issuer = new Es256TokenIssuer(
            JwtSigningKey.FromPem(TestSigningKey.Pem),
            Options.Create(new JwtOptions { Jwt = new JwtOptions.JwtSection { Issuer = "nts-Test", Audience = "nts-api" } }));
        var store = new EfRefreshTokenService(first, issuer, Options.Create(new SessionOptions()), clock);
        var otherStore = new EfRefreshTokenService(second, issuer, Options.Create(new SessionOptions()), clock);

        const string presented = "deadlock-family-token";
        var userId = Guid.CreateVersion7();

        // A real users row: refresh_tokens.user_id is a foreign key, and this test writes its row directly.
        await first.Database.ExecuteSqlAsync(
            $"INSERT INTO users (id, display_name, ui_locale, role, status, created_at, updated_at, email_confirmed, phone_number_confirmed, two_factor_enabled, lockout_enabled, access_failed_count) VALUES ({userId}, 'deadlock store', 'vi', 'user', 'active', now(), now(), true, false, false, false, 0)",
            ct);

        var row = RefreshToken.Start(
            userId,
            issuer.HashToken(presented),
            clock.UtcNow,
            clock.UtcNow.AddDays(14),
            ip: null,
            userAgent: null);

        first.RefreshTokens.Add(row);
        await first.SaveChangesAsync(ct);

        var firstUnitOfWork = new EfUnitOfWork(first);
        var secondUnitOfWork = new EfUnitOfWork(second);

        // The second transaction takes the shared probe lock; the first then takes its family lock through a
        // rotation and waits for the probe — the two now wait for each other.
        await secondUnitOfWork.BeginAsync(ct);
        await second.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(20261001)", ct);

        await firstUnitOfWork.BeginAsync(ct);
        var rotated = await store.RotateAsync(presented, new RequestInfo(null, null), ct);
        rotated.IsError.ShouldBeFalse();

        var probeWait = first.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(20261001)", ct);
        var revocation = otherStore.RevokeFamilyAsync(row.FamilyId, "logout", ct);

        // PostgreSQL kills one of the two; the losing side is either the probe statement or the revocation.
        Exception? probeFailure = null;
        try
        {
            await probeWait;
        }
        catch (Exception exception) when (exception is NpgsqlException)
        {
            probeFailure = exception;
        }

        var revoked = await revocation;

        if (probeFailure is null)
        {
            // The revocation was the victim: it must be the typed 503, not an exception.
            revoked.IsError.ShouldBeTrue();
            revoked.FirstError.Code.ShouldBe(ErrorCodes.ServiceUnavailable);
        }
        else
        {
            // The probe statement lost instead, so the revocation itself completed.
            revoked.IsError.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task ReauthFailure_WhenTheStatementDeadlocks_ReturnsServiceUnavailable()
    {
        var ct = TestContext.Current.CancellationToken;

        using var first = fixture.CreateAppDbContext();
        using var second = fixture.CreateAppDbContext();

        var clock = new FakeClock();
        var issuer = NewIssuer();
        var store = new EfRefreshTokenService(first, issuer, Options.Create(new SessionOptions()), clock);
        var otherStore = new EfRefreshTokenService(second, issuer, Options.Create(new SessionOptions()), clock);

        var row = await SeedFamilyAsync(first, issuer, "deadlock-reauth-token", ct);

        // The same deadlock choreography as the revocation test: one side holds the family lock and waits for
        // the probe, the other holds the probe and wants the family.
        var firstUnitOfWork = new EfUnitOfWork(first);
        var secondUnitOfWork = new EfUnitOfWork(second);

        await secondUnitOfWork.BeginAsync(ct);
        await second.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(20261002)", ct);

        await firstUnitOfWork.BeginAsync(ct);
        var rotated = await store.RotateAsync("deadlock-reauth-token", new RequestInfo(null, null), ct);
        rotated.IsError.ShouldBeFalse();

        var probeWait = first.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(20261002)", ct);
        var failure = otherStore.RegisterReauthFailureAsync(row.FamilyId, clock.UtcNow, ct);

        Exception? probeFailure = null;
        try
        {
            await probeWait;
        }
        catch (Exception exception) when (exception is NpgsqlException)
        {
            probeFailure = exception;
        }

        var counted = await failure;

        if (probeFailure is null)
        {
            counted.IsError.ShouldBeTrue();
            counted.FirstError.Code.ShouldBe(ErrorCodes.ServiceUnavailable);
        }
        else
        {
            counted.IsError.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Lookup_WhenTheDatabaseIsUnreachable_ReturnsServiceUnavailable()
    {
        // The lookup is the one store member that needs no transaction, so its transient failure is induced
        // directly: a server that refuses the connection. A null here would have signed a working client out
        // with a 401 instead of answering 503 (CR-2).
        using var context = TestDbContextFactory.CreateAppContext(
            "Host=127.0.0.1;Port=1;Database=nts_unreachable;Username=nts;Password=nts;Timeout=1",
            new AnonymousCurrentUser());

        var store = new EfRefreshTokenService(
            context,
            NewIssuer(),
            Options.Create(new SessionOptions()),
            new FakeClock());

        var result = await store.FindAsync("never-issued-token", CancellationToken.None);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.ServiceUnavailable);
    }

    private static Es256TokenIssuer NewIssuer() =>
        new(
            JwtSigningKey.FromPem(TestSigningKey.Pem),
            Options.Create(new JwtOptions
            {
                Jwt = new JwtOptions.JwtSection { Issuer = "nts-Test", Audience = "nts-api" },
            }));

    private static async Task<RefreshToken> SeedFamilyAsync(
        AppDbContext context,
        Es256TokenIssuer issuer,
        string presented,
        CancellationToken ct)
    {
        var now = new FakeClock().UtcNow;
        var userId = Guid.CreateVersion7();

        await context.Database.ExecuteSqlAsync(
            $"INSERT INTO users (id, display_name, ui_locale, role, status, created_at, updated_at, email_confirmed, phone_number_confirmed, two_factor_enabled, lockout_enabled, access_failed_count) VALUES ({userId}, 'deadlock store', 'vi', 'user', 'active', now(), now(), true, false, false, false, 0)",
            ct);

        var row = RefreshToken.Start(userId, issuer.HashToken(presented), now, now.AddDays(14), ip: null, userAgent: null);
        context.RefreshTokens.Add(row);
        await context.SaveChangesAsync(ct);

        return row;
    }
}
