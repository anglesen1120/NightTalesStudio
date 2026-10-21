using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.Http;
using NightTales.Api.IntegrationTests.Email;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Api.IntegrationTests.Persistence;
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
/// <c>POST /auth/forgot-password</c> and <c>POST /auth/reset-password</c> of 09.3.3-T1 (Tests table,
/// 09 README BD3/BD16/WT17/WT30/WT40): the enumeration-neutral 202, the token-free queue, the send-time
/// mint and the full reset.
/// </summary>
/// <remarks>
/// Every response's committed state is asserted as well ([RULED — README §B10 WT29]): a success-typed
/// outcome that left nothing behind is exactly the bug these tests exist to catch.
/// </remarks>
[Collection("Hangfire server")]
public sealed class PasswordResetTests(TestDatabase database) : IntegrationTest(database)
{
    private const string ForgotPath = "/api/v1/auth/forgot-password";

    private const string ResetPath = "/api/v1/auth/reset-password";

    /// <summary>A BD2-valid password different from the fixture's, for the post-reset sign-in.</summary>
    private const string NewPassword = "Zyxwvutsrqpo";

    /// <summary>The second concurrent attempt's password.</summary>
    private const string OtherPassword = "Qwertyuiopas";

    /// <summary>
    /// A password the bundled placeholder top-list contains (its SHA-1 is
    /// <c>sha1("nts-blocked-placeholder-000042")</c>, asserted by 09.3.1-T1's own unit test), so the
    /// breach check really fires.
    /// </summary>
    private const string BreachedPassword = "nts-blocked-placeholder-000042";

    [Fact]
    public async Task ForgotPassword_UnknownEmail_SameResponseAndTimingBudget()
    {
        var ct = TestContextToken.Value;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var client = factory.AnonymousClient();

        var unknownWatch = Stopwatch.StartNew();
        var unknown = await client.PostAsJsonAsync(ForgotPath, new { email = UnknownEmail() }, ct);
        unknownWatch.Stop();

        var knownWatch = Stopwatch.StartNew();
        var known = await client.PostAsJsonAsync(ForgotPath, new { email = user.Email }, ct);
        knownWatch.Stop();

        unknown.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        known.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // Same bytes and same length: not even the body shape may betray which address exists (BD16).
        var unknownBody = Convert.ToBase64String(await unknown.Content.ReadAsByteArrayAsync(ct));
        var knownBody = Convert.ToBase64String(await known.Content.ReadAsByteArrayAsync(ct));
        unknownBody.ShouldBe(knownBody);
        unknown.Content.Headers.ContentLength.ShouldBe(known.Content.Headers.ContentLength);

        // WT40: both paths are padded to the 300 ms floor.
        unknownWatch.Elapsed.ShouldBeGreaterThanOrEqualTo(AuthTiming.Floor);
        knownWatch.Elapsed.ShouldBeGreaterThanOrEqualTo(AuthTiming.Floor);

        // Only the known address queued a message, wrote a row and minted nothing yet (WT17/WT30).
        SqlProbe
            .Count(Database, $"SELECT count(*) FROM email_messages WHERE template = 'password_reset' AND user_id = '{user.Id}'")
            .ShouldBe(1);
        SqlProbe
            .Count(Database, $"SELECT count(*) FROM audit_logs WHERE action = 'account.password_reset_requested' AND subject_user_id = '{user.Id}'")
            .ShouldBe(1);
        SqlProbe.Count(Database, $"SELECT count(*) FROM user_action_tokens WHERE user_id = '{user.Id}'").ShouldBe(0);
    }

    [Fact]
    public async Task ForgotPassword_ExistingEmail_QueuesTokenFreeJob()
    {
        var ct = TestContextToken.Value;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await AuthTestUserAsync(factory);

        var response = await factory.AnonymousClient().PostAsJsonAsync(ForgotPath, new { email = user.Email }, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var parameters = SqlProbe.ScalarText(
            Database,
            $"SELECT coalesce(params::text, '') FROM email_messages WHERE user_id = '{user.Id}' AND template = 'password_reset'");

        parameters.ShouldContain("displayName");
        parameters.ShouldContain("expiresMinutes");
        parameters.ShouldContain("60");
        parameters.ShouldContain(user.DisplayName);

        // `link` is a minter key, never a producer key: the queued job carries no token (WT17/WT30).
        parameters.ShouldNotContain("link");
        parameters.ShouldNotContain("t=");

        SqlProbe.Count(Database, $"SELECT count(*) FROM user_action_tokens WHERE user_id = '{user.Id}'").ShouldBe(0);
        SqlProbe
            .ScalarText(Database, $"SELECT coalesce(payload::text, '') FROM outbox_messages WHERE user_id = '{user.Id}'")
            .ShouldNotContain("t=", Case.Sensitive, "the outbox payload never holds a token");
    }

    [Fact]
    public async Task ForgotPassword_KnownEmail_CommitsAuditAndMailRowBefore202()
    {
        var ct = TestContextToken.Value;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await AuthTestUserAsync(factory);

        var response = await factory.AnonymousClient().PostAsJsonAsync(ForgotPath, new { email = user.Email }, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // Read straight after the response: everything below is already committed, not queued in memory.
        SqlProbe
            .Count(Database, $"SELECT count(*) FROM audit_logs WHERE action = 'account.password_reset_requested' AND subject_user_id = '{user.Id}'")
            .ShouldBe(1);
        SqlProbe
            .Count(Database, $"SELECT count(*) FROM email_messages WHERE user_id = '{user.Id}' AND template = 'password_reset' AND status = 'queued'")
            .ShouldBe(1);
        SqlProbe
            .Count(Database, $"SELECT count(*) FROM outbox_messages WHERE type = 'email_requested.v1' AND user_id = '{user.Id}'")
            .ShouldBe(1, "the queue row commits with the message (D11)");

        // The row is user-visible (BD15/C3.4) and its data carries the peppered hash, never the address.
        SqlProbe
            .ScalarText(Database, $"SELECT visibility FROM audit_logs WHERE action = 'account.password_reset_requested' AND subject_user_id = '{user.Id}'")
            .ShouldBe("user");
        var data = SqlProbe.ScalarText(
            Database,
            $"SELECT coalesce(data::text, '') FROM audit_logs WHERE action = 'account.password_reset_requested' AND subject_user_id = '{user.Id}'");
        data.ShouldContain("emailHash");
        data.ShouldNotContain(user.Email!, Case.Sensitive, "an audit row never carries the address (BD16)");
    }

    [Fact]
    public async Task SendEmailJob_MintsResetToken_LinksOnlyInTheBody_ParamsNulledAfterSent()
    {
        var ct = TestContextToken.Value;

        // An isolated database: this test reads the only message the transport ever saw, so a sibling
        // test's queued message cannot become `LastMessage`.
        await using var isolated = new TestDatabase();
        await isolated.InitializeAsync();

        var clock = new FakeClock();
        var user = await isolated.CreateUserBuilder().CreateAsync(ct: ct);

        using var factory = new ApiFactory(isolated, clock);
        var response = await factory
            .AnonymousClient()
            .PostAsJsonAsync(ForgotPath, new { email = user.Email }, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var messageId = SqlProbe.ScalarText(
            isolated,
            $"SELECT id::text FROM email_messages WHERE user_id = '{user.Id}' AND template = 'password_reset'");
        messageId.ShouldNotBeNullOrWhiteSpace();

        // Nothing token-bearing exists while the message is only queued (WT17/WT30).
        SqlProbe.Count(isolated, "SELECT count(*) FROM user_action_tokens").ShouldBe(0);
        SqlProbe
            .ScalarText(isolated, $"SELECT coalesce(params::text, '') FROM email_messages WHERE id = '{messageId}'")
            .ShouldNotContain("t=");

        // The Worker graph, with the real token store restored in place of 09.2.6-T3's fake.
        await using var harness = await EmailHarness.StartAsync(
            isolated,
            clock,
            ct,
            host => host.ConfigureServices(services =>
            {
                services.RemoveAll<IUserActionTokenService>();
                services.AddScoped<IUserActionTokenService, EfUserActionTokenService>();
            }));

        await harness.DeliverAsync(ct);

        var message = harness.Smtp.Sent.Single();
        var body = message.TextBody;
        body.ShouldNotBeNull();

        var match = Regex.Match(body, @"#t=(?<token>[A-Za-z0-9_\-]+)");
        match.Success.ShouldBeTrue($"the body must carry the fragment link, but was: {body}");

        var token = match.Groups["token"].Value;
        token.ShouldNotBeNullOrWhiteSpace();
        message.HtmlBody.ShouldNotBeNull().ShouldContain($"#t={token}");

        // The row stores the hash only; the plaintext exists nowhere in the database.
        SqlProbe
            .ScalarText(isolated, "SELECT token_hash FROM user_action_tokens")
            .ShouldBe(Hash(token));
        SqlProbe.Count(isolated, $"SELECT count(*) FROM user_action_tokens WHERE token_hash = '{token}'").ShouldBe(0);

        // The row reached `sent` with its render values dropped, and the queue never held the token.
        SqlProbe
            .Count(isolated, $"SELECT count(*) FROM email_messages WHERE id = '{messageId}' AND status = 'sent' AND params IS NULL")
            .ShouldBe(1);
        SqlProbe
            .ScalarText(isolated, "SELECT coalesce(payload::text, '') FROM outbox_messages")
            .ShouldNotContain(token, Case.Sensitive);
    }

    [Fact]
    public async Task Reset_UsesToken_RevokesAllFamilies_RotatesStamp_AndQueuesNotice()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestUserAsync(factory);

        await AuthTestFlow.SeedFamilyAsync(Database, user, "family-root-token", clock.UtcNow, TimeSpan.FromDays(14));

        var hashBefore = PasswordHash(user.Id);
        var stampBefore = SecurityStamp(user.Id);

        var issued = await AuthTestFlow.IssueActionTokenAsync(factory.Services, user.Id, UserActionKind.PasswordReset, ct);
        issued.IsError.ShouldBeFalse();

        var response = await factory
            .AnonymousClient()
            .PostAsJsonAsync(ResetPath, new { token = issued.Value, newPassword = NewPassword }, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        PasswordHash(user.Id).ShouldNotBe(hashBefore, "the account got a new password hash");
        SecurityStamp(user.Id).ShouldNotBe(stampBefore, "the security stamp rotated (09.3.2-T2/D9)");

        // WF12: every family of the account dies, with the reset as the reason.
        SqlProbe
            .Count(Database, $"SELECT count(*) FROM refresh_tokens WHERE user_id = '{user.Id}' AND revoked_reason = 'password_reset'")
            .ShouldBe(1);
        SqlProbe
            .Count(Database, $"SELECT count(*) FROM refresh_tokens WHERE user_id = '{user.Id}' AND revoked_at IS NULL")
            .ShouldBe(0);

        SqlProbe
            .Count(Database, $"SELECT count(*) FROM audit_logs WHERE subject_user_id = '{user.Id}' AND action = 'account.password_reset'")
            .ShouldBe(1);
        SqlProbe
            .Count(Database, $"SELECT count(*) FROM email_messages WHERE user_id = '{user.Id}' AND template = 'password_changed'")
            .ShouldBe(1);
        SqlProbe
            .Count(Database, $"SELECT count(*) FROM user_action_tokens WHERE user_id = '{user.Id}' AND used_at IS NOT NULL")
            .ShouldBe(1, "the token was spent");

        // The new password signs in; the reset really changed the credential.
        var (login, _) = await AuthTestFlow.LoginAsync(factory.AnonymousClient(), user.Email!, NewPassword, ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reset_UsedToken_Returns400ValidationFailed()
    {
        var ct = TestContextToken.Value;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await AuthTestUserAsync(factory);
        var client = factory.AnonymousClient();

        var issued = await AuthTestFlow.IssueActionTokenAsync(factory.Services, user.Id, UserActionKind.PasswordReset, ct);
        issued.IsError.ShouldBeFalse();

        var first = await client.PostAsJsonAsync(ResetPath, new { token = issued.Value, newPassword = NewPassword }, ct);
        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var second = await client.PostAsJsonAsync(ResetPath, new { token = issued.Value, newPassword = OtherPassword }, ct);

        second.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AuthTestFlow.ProblemCodeAsync(second)).ShouldBe(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Reset_ExpiredToken_Returns400ValidationFailed()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestUserAsync(factory);

        var issued = await AuthTestFlow.IssueActionTokenAsync(factory.Services, user.Id, UserActionKind.PasswordReset, ct);
        issued.IsError.ShouldBeFalse();

        clock.Advance(TimeSpan.FromMinutes(61));

        var response = await factory
            .AnonymousClient()
            .PostAsJsonAsync(ResetPath, new { token = issued.Value, newPassword = NewPassword }, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.ValidationFailed);
        SqlProbe
            .Count(Database, $"SELECT count(*) FROM user_action_tokens WHERE user_id = '{user.Id}' AND used_at IS NULL")
            .ShouldBe(1, "the rejected reset rolled back, so the token stays unused");
    }

    [Fact]
    public async Task Reset_SuspendedUser_Returns400ValidationFailed()
    {
        var ct = TestContextToken.Value;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await AuthTestUserAsync(factory);

        await AuthTestFlow.UpdateUserAsync(factory.Services, user.Id, candidate => candidate.Status = UserStatus.Suspended);

        var issued = await AuthTestFlow.IssueActionTokenAsync(factory.Services, user.Id, UserActionKind.PasswordReset, ct);
        issued.IsError.ShouldBeFalse();

        var response = await factory
            .AnonymousClient()
            .PostAsJsonAsync(ResetPath, new { token = issued.Value, newPassword = NewPassword }, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(
            ErrorCodes.ValidationFailed,
            "a suspended account answers the same 400 as a bad token, never 'suspended'");
        SqlProbe
            .Count(Database, $"SELECT count(*) FROM user_action_tokens WHERE user_id = '{user.Id}' AND used_at IS NULL")
            .ShouldBe(1);
    }

    [Fact]
    public async Task Reset_BreachedPassword_Returns400_AndTokenStaysUsable()
    {
        var ct = TestContextToken.Value;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await AuthTestUserAsync(factory);
        var client = factory.AnonymousClient();

        var issued = await AuthTestFlow.IssueActionTokenAsync(factory.Services, user.Id, UserActionKind.PasswordReset, ct);
        issued.IsError.ShouldBeFalse();

        var breached = await client.PostAsJsonAsync(ResetPath, new { token = issued.Value, newPassword = BreachedPassword }, ct);

        breached.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AuthTestFlow.ProblemCodeAsync(breached)).ShouldBe(ErrorCodes.BreachedPassword);
        SqlProbe
            .Count(Database, $"SELECT count(*) FROM user_action_tokens WHERE user_id = '{user.Id}' AND used_at IS NULL")
            .ShouldBe(1, "the policy runs before the consume, so the user can retry with a good password");

        // The retry with a good password succeeds with the very same token.
        var retry = await client.PostAsJsonAsync(ResetPath, new { token = issued.Value, newPassword = NewPassword }, ct);
        retry.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Reset_ShortPassword_Returns400ValidationFailed_AndTokenStaysUsable()
    {
        var ct = TestContextToken.Value;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await AuthTestUserAsync(factory);
        var client = factory.AnonymousClient();

        var issued = await AuthTestFlow.IssueActionTokenAsync(factory.Services, user.Id, UserActionKind.PasswordReset, ct);
        issued.IsError.ShouldBeFalse();

        var shortResponse = await client.PostAsJsonAsync(ResetPath, new { token = issued.Value, newPassword = "short" }, ct);

        shortResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AuthTestFlow.ProblemCodeAsync(shortResponse)).ShouldBe(ErrorCodes.ValidationFailed);
        SqlProbe
            .Count(Database, $"SELECT count(*) FROM user_action_tokens WHERE user_id = '{user.Id}' AND used_at IS NULL")
            .ShouldBe(1);

        var retry = await client.PostAsJsonAsync(ResetPath, new { token = issued.Value, newPassword = NewPassword }, ct);
        retry.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Reset_ConcurrentRequests_OnlyOneSucceeds()
    {
        var ct = TestContextToken.Value;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await AuthTestUserAsync(factory);
        var client = factory.AnonymousClient();

        var issued = await AuthTestFlow.IssueActionTokenAsync(factory.Services, user.Id, UserActionKind.PasswordReset, ct);
        issued.IsError.ShouldBeFalse();

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync(ResetPath, new { token = issued.Value, newPassword = NewPassword }, ct),
            client.PostAsJsonAsync(ResetPath, new { token = issued.Value, newPassword = OtherPassword }, ct));

        responses.Count(response => response.StatusCode == HttpStatusCode.NoContent).ShouldBe(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.BadRequest).ShouldBe(1);

        var loser = responses.Single(response => response.StatusCode == HttpStatusCode.BadRequest);
        (await AuthTestFlow.ProblemCodeAsync(loser)).ShouldBe(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Reset_HoldsTheUserLock_BeforeChangingTheUser()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await AuthTestUserAsync(factory);

        var hashBefore = PasswordHash(user.Id);
        var stampBefore = SecurityStamp(user.Id);

        var issued = await AuthTestFlow.IssueActionTokenAsync(factory.Services, user.Id, UserActionKind.PasswordReset, ct);
        issued.IsError.ShouldBeFalse();

        // Hold the account's lifecycle lock — the one the MFA lifecycle takes first, seed 1 — so the reset
        // stops before it loads or changes the user.
        using var blockerScope = factory.Services.CreateScope();
        var blockerUnitOfWork = blockerScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var blockerContext = blockerScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await blockerUnitOfWork.BeginAsync(ct);
        await blockerContext.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended(CAST({user.Id} AS text), 1))",
            ct);

        var reset = factory
            .AnonymousClient()
            .PostAsJsonAsync(ResetPath, new { token = issued.Value, newPassword = NewPassword }, ct);

        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        reset.IsCompleted.ShouldBeFalse("the reset waits for the account's lifecycle lock");

        // While it waits nothing of the account has changed: the lock is taken before the user is loaded.
        PasswordHash(user.Id).ShouldBe(hashBefore, "the password is untouched while the lock is held");
        SecurityStamp(user.Id).ShouldBe(stampBefore);

        (await blockerUnitOfWork.CommitAsync(ct)).IsError.ShouldBeFalse();

        var response = await reset;
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, "the reset completes once the lock is free");
        PasswordHash(user.Id).ShouldNotBe(hashBefore);
        SecurityStamp(user.Id).ShouldNotBe(stampBefore);
    }

    [Fact]
    public async Task Reset_ConcurrentWithMfaChange_DoNotDeadlock()
    {
        var ct = TestContextToken.Value;
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);

        // Several rounds so the two transactions overlap in different ways.
        for (var round = 0; round < 5; round++)
        {
            var user = await AuthTestUserAsync(factory);
            _ = await MfaTestFlow.EnrollAsync(factory, Database, user, clock, ct);

            var callerFamily = await MfaTestFlow.SeedFamilyAsync(
                Database, user, clock.UtcNow, reauthenticatedAt: clock.UtcNow);

            var hashBefore = PasswordHash(user.Id);

            var issued = await AuthTestFlow.IssueActionTokenAsync(
                factory.Services, user.Id, UserActionKind.PasswordReset, ct);
            issued.IsError.ShouldBeFalse();

            using var client = factory.ClientFor(user.Id, user.Role, sessionId: callerFamily);

            // The reset takes the account's lifecycle lock and then the family locks; the MFA change takes
            // the same lock first. Without one order they can deadlock (40P01); with it they serialize.
            var reset = factory
                .AnonymousClient()
                .PostAsJsonAsync(ResetPath, new { token = issued.Value, newPassword = NewPassword }, ct);
            var disable = client.DeleteAsync(MfaTestFlow.MfaPath, ct);

            var responses = await Task.WhenAll(reset, disable);

            responses[0].StatusCode.ShouldBe(
                HttpStatusCode.NoContent,
                "the reset completes, never a 5xx from a deadlock (40P01)");
            responses[1].StatusCode.ShouldBe(
                HttpStatusCode.NoContent,
                "the MFA change completes, never a 5xx from a deadlock (40P01)");

            // Both state invariants hold whichever transaction committed first.
            PasswordHash(user.Id).ShouldNotBe(hashBefore, "the reset applied");
            SqlProbe
                .Count(Database, $"SELECT count(*) FROM refresh_tokens WHERE user_id = '{user.Id}' AND revoked_at IS NULL")
                .ShouldBe(0, "every family of the account is revoked");
            SqlProbe
                .ScalarText(Database, $"SELECT two_factor_enabled::text FROM users WHERE id = '{user.Id}'")
                .ShouldBe("false", "the MFA disable applied");
            SqlProbe
                .Count(Database, $"SELECT count(*) FROM user_tokens WHERE user_id = '{user.Id}'")
                .ShouldBe(0, "the enrolment secret is gone");
            SqlProbe
                .Count(Database, $"SELECT count(*) FROM user_action_tokens WHERE user_id = '{user.Id}' AND used_at IS NOT NULL")
                .ShouldBe(1, "the reset token was spent");
        }
    }

    [Fact]
    public async Task Reset_WhenTokenStoreUnavailable_Returns503_AndChangesNothing()
    {
        var ct = TestContextToken.Value;
        using var host = ApiFactory.ProductionHost(
            Database.ConnectionString,
            builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<IUserActionTokenService>();
                services.AddScoped<EfUserActionTokenService>();
                services.AddScoped<IUserActionTokenService>(provider =>
                    new ConsumeUnavailableTokenService(provider.GetRequiredService<EfUserActionTokenService>()));
            }));

        var user = await AuthTestFlow.CreateUserAsync(host.Services);
        var hashBefore = PasswordHash(user.Id);
        var stampBefore = SecurityStamp(user.Id);

        var issued = await AuthTestFlow.IssueActionTokenAsync(host.Services, user.Id, UserActionKind.PasswordReset, ct);
        issued.IsError.ShouldBeFalse();

        var response = await host
            .CreateClient()
            .PostAsJsonAsync(ResetPath, new { token = issued.Value, newPassword = NewPassword }, ct);

        // PW-CONSUME-003: a transient store failure stays the 503 ConsumeAsync mapped, never the 400 that
        // tells the user the link is bad.
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.ServiceUnavailable);

        // Nothing was consumed, changed or recorded.
        SqlProbe
            .Count(Database, $"SELECT count(*) FROM user_action_tokens WHERE user_id = '{user.Id}' AND used_at IS NULL")
            .ShouldBe(1, "the token is still spendable");
        PasswordHash(user.Id).ShouldBe(hashBefore);
        SecurityStamp(user.Id).ShouldBe(stampBefore);
        SqlProbe
            .Count(Database, $"SELECT count(*) FROM audit_logs WHERE subject_user_id = '{user.Id}' AND action = 'account.password_reset'")
            .ShouldBe(0);
        SqlProbe
            .Count(Database, $"SELECT count(*) FROM email_messages WHERE user_id = '{user.Id}' AND template = 'password_changed'")
            .ShouldBe(0);
    }

    [Fact]
    public async Task Reset_WhenFamilyRevocationFails_Returns503_AndRollsBackTheReset()
    {
        var ct = TestContextToken.Value;
        using var host = ApiFactory.ProductionHost(
            Database.ConnectionString,
            builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<IUserActionTokenService>();
                services.AddScoped<EfUserActionTokenService>();
                services.AddScoped<IUserActionTokenService>(provider =>
                    new PassThroughTokenService(provider.GetRequiredService<EfUserActionTokenService>()));

                services.RemoveAll<IRefreshTokenService>();
                services.AddScoped<IRefreshTokenService>(provider =>
                    new RevokeUnavailableRefreshTokenService(provider.GetRequiredService<EfRefreshTokenService>()));
            }));

        var user = await AuthTestFlow.CreateUserAsync(host.Services);
        await AuthTestFlow.SeedFamilyAsync(Database, user, "reset-revoke-family", DateTimeOffset.UtcNow, TimeSpan.FromDays(14));

        var hashBefore = PasswordHash(user.Id);
        var stampBefore = SecurityStamp(user.Id);

        var issued = await AuthTestFlow.IssueActionTokenAsync(host.Services, user.Id, UserActionKind.PasswordReset, ct);
        issued.IsError.ShouldBeFalse();

        var response = await host
            .CreateClient()
            .PostAsJsonAsync(ResetPath, new { token = issued.Value, newPassword = NewPassword }, ct);

        // PW-REVOKE-004: a refused family revocation fails the whole reset, so the UnitOfWork rolls the
        // spent token, the password and the stamp back with it.
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await AuthTestFlow.ProblemCodeAsync(response)).ShouldBe(ErrorCodes.ServiceUnavailable);

        SqlProbe
            .Count(Database, $"SELECT count(*) FROM user_action_tokens WHERE user_id = '{user.Id}' AND used_at IS NULL")
            .ShouldBe(1, "the token consumption rolled back");
        PasswordHash(user.Id).ShouldBe(hashBefore, "the password change rolled back");
        SecurityStamp(user.Id).ShouldBe(stampBefore, "the stamp rotation rolled back");
        SqlProbe
            .Count(Database, $"SELECT count(*) FROM refresh_tokens WHERE user_id = '{user.Id}' AND revoked_at IS NULL")
            .ShouldBe(1, "the session the reset was meant to end is still live");
        SqlProbe
            .Count(Database, $"SELECT count(*) FROM audit_logs WHERE subject_user_id = '{user.Id}' AND action = 'account.password_reset'")
            .ShouldBe(0, "nothing is audited when the revocation failed");
        SqlProbe
            .Count(Database, $"SELECT count(*) FROM email_messages WHERE user_id = '{user.Id}' AND template = 'password_changed'")
            .ShouldBe(0, "no success notice is queued when the revocation failed");
    }

    [Fact]
    public async Task Reset_ForAdmin_RaisesSecurityAlert()
    {
        var ct = TestContextToken.Value;
        var alerter = new CapturingSecurityAlerter();

        // The production composition root: this test replaces exactly one port, the alerter.
        using var host = ApiFactory.ProductionHost(
            Database.ConnectionString,
            builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISecurityAlerter>();
                services.AddSingleton<ISecurityAlerter>(alerter);
            }));

        var user = await AuthTestFlow.CreateUserAsync(host.Services, role: PlatformRole.Admin);
        var issued = await AuthTestFlow.IssueActionTokenAsync(host.Services, user.Id, UserActionKind.PasswordReset, ct);
        issued.IsError.ShouldBeFalse();

        var response = await host
            .CreateClient()
            .PostAsJsonAsync(ResetPath, new { token = issued.Value, newPassword = NewPassword }, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        alerter.Events.Count.ShouldBe(1, "a privileged account's reset alerts the operator (Design step 7)");
        alerter.Events[0].Kind.ShouldBe(SecurityEventKinds.PasswordReset);
        alerter.Events[0].SubjectUserId.ShouldBe(user.Id);
    }

    [Fact]
    public async Task Reset_DoesNotLogTheTokenPasswordOrAddress()
    {
        var ct = TestContextToken.Value;
        var logs = new CapturingEmailLogs();
        using var factory = new ApiFactory(Database, new FakeClock()).WithLoggerProvider(logs);
        var user = await AuthTestUserAsync(factory);
        var client = factory.AnonymousClient();

        var forgot = await client.PostAsJsonAsync(ForgotPath, new { email = user.Email }, ct);
        forgot.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var issued = await AuthTestFlow.IssueActionTokenAsync(factory.Services, user.Id, UserActionKind.PasswordReset, ct);
        issued.IsError.ShouldBeFalse();

        var reset = await client.PostAsJsonAsync(ResetPath, new { token = issued.Value, newPassword = NewPassword }, ct);
        reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var text = string.Join('\n', logs.Messages);

        // Non-vacuous: the host really rendered the mediator log line this scan reads.
        text.ShouldContain("ForgotPasswordCommand", Case.Sensitive, "the mediator log line is what this scan reads");
        text.ShouldContain("ResetPasswordCommand", Case.Sensitive);

        text.ShouldNotContain(issued.Value, Case.Sensitive, "a reset token must never be logged (BD16)");
        text.ShouldNotContain(NewPassword, Case.Sensitive, "a password must never be logged (BD16)");
        text.ShouldNotContain(user.Email!, Case.Sensitive, "an address must never be logged (BD16/D17)");

        // The reset row carries no payload at all (BD15/C3.4's `{}` allow-list).
        SqlProbe
            .ScalarText(Database, $"SELECT coalesce(data::text, '') FROM audit_logs WHERE subject_user_id = '{user.Id}' AND action = 'account.password_reset'")
            .ShouldBe("{}");
    }

    /// <summary>One user with a real Identity password, created through the host's own store.</summary>
    private static async Task<AppUser> AuthTestUserAsync(ApiFactory factory) =>
        await AuthTestFlow.CreateUserAsync(factory.Services);

    private static string UnknownEmail() => $"nobody-{Guid.CreateVersion7():N}@nighttales.test";

    private static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private string PasswordHash(Guid userId) =>
        SqlProbe.ScalarText(Database, $"SELECT password_hash FROM users WHERE id = '{userId}'");

    private string SecurityStamp(Guid userId) =>
        SqlProbe.ScalarText(Database, $"SELECT security_stamp FROM users WHERE id = '{userId}'");

    /// <summary>
    /// Wraps the real action-token store, except that a consume answers the transient 503 the review
    /// finding is about (PW-CONSUME-003); the issue still mints the token the request will present.
    /// </summary>
    private sealed class ConsumeUnavailableTokenService(IUserActionTokenService inner) : IUserActionTokenService
    {
        public Task<ErrorOr<string>> IssueAsync(
            Guid userId,
            UserActionKind kind,
            RequestInfo info,
            CancellationToken cancellationToken) =>
            inner.IssueAsync(userId, kind, info, cancellationToken);

        public Task<ErrorOr<string>> IssueAsync(
            Guid userId,
            UserActionKind kind,
            Guid subjectId,
            DateTimeOffset expiresAt,
            RequestInfo info,
            CancellationToken cancellationToken) =>
            inner.IssueAsync(userId, kind, subjectId, expiresAt, info, cancellationToken);

        public Task<ErrorOr<ConsumedActionToken>> ConsumeAsync(string token, UserActionKind kind, CancellationToken cancellationToken) =>
            Task.FromResult<ErrorOr<ConsumedActionToken>>(
                Error.Failure(
                    code: ErrorCodes.ServiceUnavailable,
                    description: "The database is temporarily unavailable; retry."));
    }

    /// <summary>The real action-token store, unchanged: the revoke test needs no consume fault.</summary>
    private sealed class PassThroughTokenService(IUserActionTokenService inner) : IUserActionTokenService
    {
        public Task<ErrorOr<string>> IssueAsync(
            Guid userId,
            UserActionKind kind,
            RequestInfo info,
            CancellationToken cancellationToken) =>
            inner.IssueAsync(userId, kind, info, cancellationToken);

        public Task<ErrorOr<string>> IssueAsync(
            Guid userId,
            UserActionKind kind,
            Guid subjectId,
            DateTimeOffset expiresAt,
            RequestInfo info,
            CancellationToken cancellationToken) =>
            inner.IssueAsync(userId, kind, subjectId, expiresAt, info, cancellationToken);

        public Task<ErrorOr<ConsumedActionToken>> ConsumeAsync(string token, UserActionKind kind, CancellationToken cancellationToken) =>
            inner.ConsumeAsync(token, kind, cancellationToken);
    }

    /// <summary>
    /// Wraps the real refresh-token store, except that the family revocation answers the transient 503 the
    /// review finding is about (PW-REVOKE-004).
    /// </summary>
    private sealed class RevokeUnavailableRefreshTokenService(IRefreshTokenService inner) : IRefreshTokenService
    {
        public Task<ErrorOr<IssuedRefreshSession>> StartSessionAsync(
            AppUser user,
            RequestInfo info,
            CancellationToken ct) =>
            inner.StartSessionAsync(user, info, ct);

        public Task<ErrorOr<RotatedSession>> RotateAsync(string presentedToken, RequestInfo info, CancellationToken ct) =>
            inner.RotateAsync(presentedToken, info, ct);

        public Task<ErrorOr<Success>> RevokeFamilyAsync(Guid familyId, string reason, CancellationToken ct) =>
            inner.RevokeFamilyAsync(familyId, reason, ct);

        public Task<ErrorOr<int>> RevokeAllFamiliesAsync(
            Guid userId,
            string reason,
            Guid? exceptFamilyId,
            CancellationToken ct) =>
            Task.FromResult<ErrorOr<int>>(
                Error.Failure(
                    code: ErrorCodes.ServiceUnavailable,
                    description: "The database is temporarily unavailable; retry."));

        public Task<ErrorOr<Success>> MarkReauthenticatedAsync(Guid familyId, DateTimeOffset now, CancellationToken ct) =>
            inner.MarkReauthenticatedAsync(familyId, now, ct);

        public Task<ErrorOr<int>> RegisterReauthFailureAsync(Guid familyId, DateTimeOffset now, CancellationToken ct) =>
            inner.RegisterReauthFailureAsync(familyId, now, ct);

        public Task<ErrorOr<ReauthState>> GetReauthStateAsync(Guid familyId, CancellationToken ct) =>
            inner.GetReauthStateAsync(familyId, ct);
    }

    /// <summary>Captures every alert the flow raises; the seam the admin-reset test asserts through.</summary>
    private sealed class CapturingSecurityAlerter : ISecurityAlerter
    {
        private readonly List<SecurityEvent> _events = [];

        internal List<SecurityEvent> Events => _events;

        public Task AlertAsync(SecurityEvent evt, CancellationToken ct)
        {
            _events.Add(evt);

            return Task.CompletedTask;
        }
    }
}
