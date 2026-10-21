using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Security;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Auth;

/// <summary>
/// Re-authentication of 09.10.4-T1 (Tests table): the family stamp, the durable five-attempt death of
/// BD4, the alert on an owner's or admin's first failure, and the guard that reads the live family.
/// </summary>
/// <remarks>
/// Every 401 here asserts the committed state too ([RULED — README §B10 WT29]): a failed
/// re-authentication returns a <em>value</em>, so its counter, its possible revocation and its audit row
/// must survive the 401.
/// </remarks>
public sealed class ReauthTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reauth_SetsReauthenticatedAt()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        var familyId = await MfaTestFlow.SeedFamilyAsync(Database, user, clock.UtcNow);
        using var client = factory.ClientFor(user.Id, user.Role, sessionId: familyId);

        var response = await client.PostAsJsonAsync(
            MfaTestFlow.ReauthPath,
            new { password = MfaTestFlow.Password },
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await MfaTestFlow.JsonAsync(response, Ct);
        var reauthenticatedAt = body.GetProperty("reauthenticatedAt").GetDateTimeOffset();
        reauthenticatedAt.ShouldBe(clock.UtcNow, TimeSpan.FromSeconds(1));

        (await StoredReauthenticatedAtAsync(familyId)).ShouldNotBeNull(
            "the instant is stamped on the family's live row");
    }

    [Fact]
    public async Task Reauth_FiveWrongAttempts_RevokesFamily()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var enrolment = await MfaTestFlow.EnrollAsync(factory, Database, user, clock, Ct);

        var familyId = await MfaTestFlow.SeedFamilyAsync(
            Database,
            user,
            clock.UtcNow,
            reauthenticatedAt: clock.UtcNow);

        using var client = factory.ClientFor(user.Id, user.Role, sessionId: familyId);

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var wrong = await WrongCodeAsync(client, enrolment.Secret, clock);

            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await AuthTestFlow.ProblemCodeAsync(wrong)).ShouldBe(ErrorCodes.InvalidMfaCode);
        }

        var fifth = await WrongCodeAsync(client, enrolment.Secret, clock);

        fifth.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(fifth)).ShouldBe(
            ErrorCodes.SessionRevoked,
            "the fifth wrong attempt ends the family (BD4)");

        await using var fresh = Database.CreateAppDbContext();
        var row = await fresh.RefreshTokens
            .AsNoTracking()
            .SingleAsync(candidate => candidate.FamilyId == familyId, Ct);

        row.ReauthAttempts.ShouldBe((short)5, "the counter is durable, not per instance (WT32)");
        row.RevokedAt.ShouldNotBeNull();

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT count(*)::text FROM audit_logs WHERE action = '{MfaTestFlow.ReauthFailedAction}' "
                + $"AND subject_user_id = '{user.Id}'"))
            .ShouldBe("5", "every failure committed its row before the 401 (WT29)");
    }

    [Fact]
    public async Task Reauth_WhenFamilyRevoked_ReturnsSessionRevoked()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        var familyId = await MfaTestFlow.SeedFamilyAsync(
            Database,
            user,
            clock.UtcNow,
            reauthenticatedAt: clock.UtcNow);

        await Database.ExecuteAsync(
            $"UPDATE refresh_tokens SET revoked_at = now(), revoked_reason = 'logout' "
            + $"WHERE family_id = '{familyId}'", Ct);

        using var client = factory.ClientFor(user.Id, user.Role, sessionId: familyId);

        var response = await client.PostAsJsonAsync(
            MfaTestFlow.ReauthPath,
            new { password = MfaTestFlow.Password },
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.SessionRevoked);

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT reauth_attempts::text FROM refresh_tokens WHERE family_id = '{familyId}'"))
            .ShouldBe("0", "a dead family is charged nothing");
    }

    [Fact]
    public async Task Reauth_FirstAdminFailure_RaisesSecurityAlert()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services, role: PlatformRole.Admin);
        var enrolment = await MfaTestFlow.EnrollAsync(factory, Database, user, clock, Ct);

        var familyId = await MfaTestFlow.SeedFamilyAsync(
            Database,
            user,
            clock.UtcNow,
            reauthenticatedAt: clock.UtcNow);

        using var client = factory.ClientFor(user.Id, PlatformRole.Admin, sessionId: familyId);

        var response = await WrongCodeAsync(client, enrolment.Secret, clock);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.InvalidMfaCode);

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT count(*)::text FROM audit_logs WHERE action = 'security.reauth_failed' "
                + $"AND subject_user_id = '{user.Id}'"))
            .ShouldBe("1", "an admin's first failure raises the P alert (WT25)");

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT visibility FROM audit_logs WHERE action = 'security.reauth_failed' "
                + $"AND subject_user_id = '{user.Id}' LIMIT 1"))
            .ShouldBe("internal");
    }

    [Fact]
    public async Task Reauth_Failure_CommitsCounterAndAuditBefore401()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        var familyId = await MfaTestFlow.SeedFamilyAsync(
            Database,
            user,
            clock.UtcNow,
            reauthenticatedAt: clock.UtcNow);

        using var client = factory.ClientFor(user.Id, user.Role, sessionId: familyId);

        var response = await client.PostAsJsonAsync(
            MfaTestFlow.ReauthPath,
            new { password = "not-the-password-1" },
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.InvalidCredentials);

        await using var fresh = Database.CreateAppDbContext();
        var row = await fresh.RefreshTokens
            .AsNoTracking()
            .SingleAsync(candidate => candidate.FamilyId == familyId, Ct);

        row.ReauthAttempts.ShouldBe((short)1);
        row.ReauthLastAttemptAt.ShouldNotBeNull();

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT count(*)::text FROM audit_logs WHERE action = '{MfaTestFlow.ReauthFailedAction}' "
                + $"AND subject_user_id = '{user.Id}'"))
            .ShouldBe("1", "the row committed with the counter (WT29)");
    }

    [Fact]
    public async Task ReauthGuard_WithoutRecentReauth_ReturnsReauthRequired()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock).MapEndpoints(MfaProbeEndpoints.Map);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        var familyId = await MfaTestFlow.SeedFamilyAsync(Database, user, clock.UtcNow);
        using var client = factory.ClientFor(user.Id, user.Role, sessionId: familyId);

        var guarded = await client.GetAsync(MfaProbeEndpoints.ReauthGuardedPath, Ct);

        guarded.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(guarded)).ShouldBe(ErrorCodes.ReauthRequired);

        var reauth = await client.PostAsJsonAsync(
            MfaTestFlow.ReauthPath,
            new { password = MfaTestFlow.Password },
            Ct);
        reauth.StatusCode.ShouldBe(HttpStatusCode.OK);

        var after = await client.GetAsync(MfaProbeEndpoints.ReauthGuardedPath, Ct);

        after.StatusCode.ShouldBe(HttpStatusCode.OK, "the same route passes once the family re-authenticated");
    }

    [Fact]
    public async Task ReauthGuard_ReadsLiveFamilyState_NotTheJwt()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock).MapEndpoints(MfaProbeEndpoints.Map);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        var familyId = await MfaTestFlow.SeedFamilyAsync(
            Database,
            user,
            clock.UtcNow,
            reauthenticatedAt: clock.UtcNow);

        using var client = factory.ClientFor(user.Id, user.Role, sessionId: familyId);

        (await client.GetAsync(MfaProbeEndpoints.ReauthGuardedPath, Ct)).StatusCode
            .ShouldBe(HttpStatusCode.OK, "the family is live and recently re-authenticated");

        // The token the caller holds is unchanged; only the family's row dies.
        await Database.ExecuteAsync(
            $"UPDATE refresh_tokens SET revoked_at = now(), revoked_reason = 'logout' "
            + $"WHERE family_id = '{familyId}'", Ct);

        var guarded = await client.GetAsync(MfaProbeEndpoints.ReauthGuardedPath, Ct);

        guarded.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(guarded)).ShouldBe(
            ErrorCodes.SessionRevoked,
            "the guard reads the live family, never the token's claims");
    }

    [Fact]
    public async Task ReauthFailure_ConcurrentWithRotation_ChargesTheSuccessorAndStillReachesFive()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        const string presented = "reauth-rotation-race-token";
        var familyId = (await AuthTestFlow.SeedFamilyAsync(
            Database, user, presented, clock.UtcNow, TimeSpan.FromDays(14))).FamilyId;

        // The rotation runs in its own scope and holds the family lock inside an open transaction; the
        // failure is charged in another scope while the rotation is still uncommitted.
        await using (var rotationScope = factory.Services.CreateAsyncScope())
        {
            var rotation = rotationScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var tokens = rotationScope.ServiceProvider.GetRequiredService<IRefreshTokenService>();
            await rotation.BeginAsync(Ct);

            var rotated = await tokens.RotateAsync(presented, new RequestInfo(null, null), Ct);
            rotated.IsError.ShouldBeFalse();

            await using var failureScope = factory.Services.CreateAsyncScope();
            var failure = failureScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var failures = failureScope.ServiceProvider.GetRequiredService<IRefreshTokenService>();
            await failure.BeginAsync(Ct);

            var charged = failures.RegisterReauthFailureAsync(familyId, clock.UtcNow, Ct);
            await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
            charged.IsCompleted.ShouldBeFalse("the charge waits for the family lock the rotation holds");

            (await rotation.CommitAsync(Ct)).IsError.ShouldBeFalse();

            var result = await charged;
            result.IsError.ShouldBeFalse("the failure lands on the successor the rotation committed");
            result.Value.ShouldBe(1, "the successor inherits the family counter and takes the failure");

            (await failure.CommitAsync(Ct)).IsError.ShouldBeFalse();
        }

        // The remaining four failures through the real re-authentication reach the ceiling and revoke the
        // family: the concurrent failure was not lost.
        using var client = factory.ClientFor(user.Id, user.Role, sessionId: familyId);

        for (var attempt = 2; attempt <= 4; attempt++)
        {
            var wrong = await client.PostAsJsonAsync(
                MfaTestFlow.ReauthPath,
                new { password = "not-the-password-1" },
                Ct);

            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await AuthTestFlow.ProblemCodeAsync(wrong)).ShouldBe(
                ErrorCodes.InvalidCredentials,
                "attempts 2 to 4 are below the ceiling");
        }

        var fifth = await client.PostAsJsonAsync(
            MfaTestFlow.ReauthPath,
            new { password = "not-the-password-1" },
            Ct);

        fifth.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuthTestFlow.ProblemCodeAsync(fifth)).ShouldBe(
            ErrorCodes.SessionRevoked,
            "the fifth failure ends the family (BD4)");

        (await MfaTestFlow.ScalarAsync(
                Database,
                $"SELECT count(*)::text FROM refresh_tokens WHERE family_id = '{familyId}' "
                + "AND revoked_at IS NULL"))
            .ShouldBe("0", "no live row survives the fifth charged failure");
    }

    [Fact]
    public async Task ReauthState_WhenTheFamilyIsDead_FailsClosed()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);

        var familyId = (await AuthTestFlow.SeedFamilyAsync(
            Database, user, "dead-family-token", clock.UtcNow, TimeSpan.FromDays(14))).FamilyId;

        await Database.ExecuteAsync(
            $"UPDATE refresh_tokens SET revoked_at = now(), revoked_reason = 'logout' "
            + $"WHERE family_id = '{familyId}'", Ct);

        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var store = scope.ServiceProvider.GetRequiredService<IRefreshTokenService>();
        await unitOfWork.BeginAsync(Ct);

        var charged = await store.RegisterReauthFailureAsync(familyId, clock.UtcNow, Ct);

        charged.IsError.ShouldBeTrue("a dead family cannot be charged; fail closed, never count nothing");
        charged.FirstError.Code.ShouldBe(ErrorCodes.SessionRevoked);

        var stamped = await store.MarkReauthenticatedAsync(familyId, clock.UtcNow, Ct);

        stamped.IsError.ShouldBeTrue("a dead family cannot be stamped as re-authenticated");
        stamped.FirstError.Code.ShouldBe(ErrorCodes.SessionRevoked);

        (await unitOfWork.CommitAsync(Ct)).IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task Reauth_ConcurrentWithMfaChange_DoNotDeadlock()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);

        // Several rounds so the two transactions overlap in different ways.
        for (var round = 0; round < 5; round++)
        {
            var user = await AuthTestFlow.CreateUserAsync(factory.Services);
            var enrolment = await MfaTestFlow.EnrollAsync(factory, Database, user, clock, Ct);

            var reauthFamily = await MfaTestFlow.SeedFamilyAsync(
                Database, user, clock.UtcNow, reauthenticatedAt: clock.UtcNow);
            var changeFamily = await MfaTestFlow.SeedFamilyAsync(
                Database, user, clock.UtcNow, reauthenticatedAt: clock.UtcNow);

            using var reauthClient = factory.ClientFor(user.Id, user.Role, sessionId: reauthFamily);
            using var changeClient = factory.ClientFor(user.Id, user.Role, sessionId: changeFamily);

            // The re-authentication's TOTP compare-and-set writes the users row and then takes a family
            // lock; the disable takes the family locks and writes the users row at commit. Without one lock
            // order they can deadlock; with it they serialize.
            var reauthTask = reauthClient.PostAsJsonAsync(
                MfaTestFlow.ReauthPath,
                new { password = MfaTestFlow.Password, code = MfaTestFlow.Code(enrolment.Secret, clock.UtcNow) },
                Ct);
            var changeTask = changeClient.DeleteAsync(MfaTestFlow.MfaPath, Ct);

            var responses = await Task.WhenAll(reauthTask, changeTask);

            responses[0].StatusCode.ShouldBeOneOf(
                [HttpStatusCode.OK, HttpStatusCode.Unauthorized],
                "the re-authentication completes or is refused, never a 5xx from a deadlock (40P01)");
            responses[1].StatusCode.ShouldBe(
                HttpStatusCode.NoContent,
                "the MFA change completes, never a 5xx from a deadlock (40P01)");
        }
    }

    [Fact]
    public async Task Reauthenticate_HoldsTheUserLock_WhileItWaitsForTheFamily()
    {
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var enrolment = await MfaTestFlow.EnrollAsync(factory, Database, user, clock, Ct);

        var familyId = await MfaTestFlow.SeedFamilyAsync(
            Database, user, clock.UtcNow, reauthenticatedAt: clock.UtcNow);

        // Hold the family lock, so the re-authentication stops at MarkReauthenticatedAsync while its
        // transaction is still open — exactly the window in which the lock order matters.
        await using var blockerScope = factory.Services.CreateAsyncScope();
        var blockerUnitOfWork = blockerScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var blockerContext = blockerScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await blockerUnitOfWork.BeginAsync(Ct);
        await blockerContext.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended(CAST({familyId} AS text), 0))",
            Ct);

        using var client = factory.ClientFor(user.Id, user.Role, sessionId: familyId);

        var reauth = client.PostAsJsonAsync(
            MfaTestFlow.ReauthPath,
            new { password = MfaTestFlow.Password, code = MfaTestFlow.Code(enrolment.Secret, clock.UtcNow) },
            Ct);

        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        reauth.IsCompleted.ShouldBeFalse("the re-authentication waits for the family lock");

        // The re-authentication must hold the per-user lock (user lock -> family lock -> users row), so
        // this probe on another connection must not be able to take it.
        var held = await MfaTestFlow.ScalarAsync(
            Database,
            $"SELECT pg_try_advisory_xact_lock(hashtextextended('{user.Id}'::text, 1))::text");

        held.ShouldBe(
            "false",
            "the re-authentication takes the per-user lock first; without it this path holds the users row "
            + "and wants the family lock, the opposite order to the MFA lifecycle (deadlock)");

        (await blockerUnitOfWork.CommitAsync(Ct)).IsError.ShouldBeFalse();

        var response = await reauth;

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "the re-authentication completes once the family is free");
    }

    /// <summary>One re-authentication with a wrong authenticator code.</summary>
    private static Task<HttpResponseMessage> WrongCodeAsync(HttpClient client, string secret, FakeClock clock) =>
        client.PostAsJsonAsync(
            MfaTestFlow.ReauthPath,
            new { password = MfaTestFlow.Password, code = MfaTestFlow.WrongCode(secret, clock.UtcNow) },
            Ct);

    /// <summary>The family's stored <c>reauthenticated_at</c>, as an instant.</summary>
    private async Task<DateTimeOffset?> StoredReauthenticatedAtAsync(Guid familyId)
    {
        var raw = await MfaTestFlow.ScalarAsync(
            Database,
            $"SELECT reauthenticated_at::text FROM refresh_tokens WHERE family_id = '{familyId}'");

        return raw is null
            ? null
            : DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
    }
}
