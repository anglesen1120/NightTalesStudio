using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Application.Abstractions.Security;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Entities;
using NightTales.Infrastructure.Security;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;

namespace NightTales.Api.IntegrationTests.Auth;

/// <summary>
/// The shared plumbing of 09.10.4-T1's tests: the four MFA routes, a seeded refresh family whose
/// re-authentication state the test decides, the TOTP code of a secret, and the raw reads the task's
/// acceptance checks ask for.
/// </summary>
/// <remarks>
/// <para>
/// A test drives the routes with <see cref="ApiFactory.ClientFor"/>'s <c>X-Test-Session</c> and
/// <c>X-Test-Scope</c> headers, which the harness turns into the <c>sid</c> and <c>scope</c> claims a real
/// token carries (09 README BD3/BD4): the re-auth gate reads the family row, so a test must have one.
/// </para>
/// <para>
/// Codes are computed from the secret the API issued, with the same pure <see cref="Rfc6238"/> the
/// verifier uses and the host's own <see cref="FakeClock"/> reading, so a test never sleeps.
/// </para>
/// </remarks>
internal static class MfaTestFlow
{
    internal const string SetupPath = "/api/v1/me/mfa/setup";

    internal const string VerifyPath = "/api/v1/me/mfa/verify";

    internal const string RecoveryCodesPath = "/api/v1/me/mfa/recovery-codes";

    internal const string MfaPath = "/api/v1/me/mfa";

    internal const string ReauthPath = "/api/v1/auth/reauth";

    internal const string LoginPath = AuthTestFlow.LoginPath;

    internal const string Password = AuthTestFlow.Password;

    /// <summary>The action of the single WT35 row.</summary>
    internal const string GuessingLockedAction = "mfa.code_guessing_locked";

    /// <summary>The action of a re-authentication failure row.</summary>
    internal const string ReauthFailedAction = "account.reauth_failed";

    /// <summary>The action of the enrolment row.</summary>
    internal const string MfaEnrolledAction = "mfa.enrolled";

    /// <summary>The step seconds the host configures (BD4: 30).</summary>
    internal const int StepSeconds = Rfc6238.DefaultStepSeconds;

    /// <summary>The six-digit code of <paramref name="secret"/> for the step <paramref name="now"/> is in.</summary>
    internal static string Code(string secret, DateTimeOffset now)
    {
        Base32.TryDecode(secret, out var key).ShouldBeTrue(
            "the secret the API issued must be RFC 4648 base32, the alphabet authenticator apps accept");

        return Rfc6238.Compute(key, Rfc6238.StepAt(now, StepSeconds));
    }

    /// <summary>
    /// A six-digit code that is <em>not</em> the current one: the same digits with the first one rotated,
    /// so a wrong-code test can never accidentally submit the right code.
    /// </summary>
    internal static string WrongCode(string secret, DateTimeOffset now)
    {
        var code = Code(secret, now);
        var rotated = (char)('0' + (((code[0] - '0') + 1) % 10));

        return rotated + code[1..];
    }

    /// <summary>
    /// Seeds one live family row for <paramref name="user"/> and, when asked, stamps its
    /// <c>reauthenticated_at</c> — the state a real session has after <c>POST /auth/reauth</c>.
    /// </summary>
    internal static async Task<Guid> SeedFamilyAsync(
        TestDatabase database,
        AppUser user,
        DateTimeOffset createdAt,
        DateTimeOffset? reauthenticatedAt = null)
    {
        await using var context = database.CreateAppDbContext();

        var row = RefreshToken.Start(
            user.Id,
            AuthTestFlow.Hash($"session-{Guid.CreateVersion7():N}"),
            createdAt,
            createdAt + TimeSpan.FromDays(14),
            ip: null,
            userAgent: null);

        context.RefreshTokens.Add(row);
        await context.SaveChangesAsync();

        if (reauthenticatedAt is { } at)
        {
            await database.ExecuteAsync(
                $"UPDATE refresh_tokens SET reauthenticated_at = '{at:O}'::timestamptz "
                + $"WHERE family_id = '{row.FamilyId}'");
        }

        return row.FamilyId;
    }

    /// <summary>The active secret of the user, read through the store the API writes through.</summary>
    internal static async Task<string> ActiveSecretAsync(
        ApiFactory factory,
        Guid userId,
        CancellationToken ct)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITotpSecretStore>();
        var secret = await store.GetActiveSecretAsync(userId, ct);

        secret.IsError.ShouldBeFalse("the user must have an active secret");
        secret.Value.ShouldNotBeNull();

        return secret.Value!;
    }

    /// <summary>The pending (unconfirmed) secret of the user.</summary>
    internal static async Task<string> PendingSecretAsync(
        ApiFactory factory,
        Guid userId,
        CancellationToken ct)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITotpSecretStore>();
        var secret = await store.GetPendingSecretAsync(userId, ct);

        secret.IsError.ShouldBeFalse("the user must have a pending secret");
        secret.Value.ShouldNotBeNull();

        return secret.Value!;
    }

    /// <summary>
    /// Enrols <paramref name="user"/> through the two routes and returns what the API issued: the secret
    /// that became active and the ten recovery codes. The flow is a seeded family with a fresh
    /// re-authentication, then setup and verify with the code of the secret the API just issued.
    /// </summary>
    internal static async Task<Enrolment> EnrollAsync(
        ApiFactory factory,
        TestDatabase database,
        AppUser user,
        FakeClock clock,
        CancellationToken ct)
    {
        var familyId = await SeedFamilyAsync(database, user, clock.UtcNow, reauthenticatedAt: clock.UtcNow);
        using var client = factory.ClientFor(user.Id, user.Role, sessionId: familyId);

        var setup = await client.PostAsync(SetupPath, content: null, ct);
        setup.StatusCode.ShouldBe(HttpStatusCode.OK, "a recent re-authentication admits the enrolment route");

        var secret = await SetupSecretAsync(setup, ct);

        var verify = await client.PostAsJsonAsync(
            VerifyPath,
            new { code = Code(secret, clock.UtcNow) },
            ct);

        verify.StatusCode.ShouldBe(HttpStatusCode.OK, "a code from the pending secret confirms the enrolment");

        return new Enrolment(secret, await RecoveryCodesAsync(verify, ct));
    }

    /// <summary>What an enrolment issued.</summary>
    /// <param name="Secret">The secret that became active.</param>
    /// <param name="RecoveryCodes">The ten recovery codes, in plaintext, once.</param>
    internal sealed record Enrolment(string Secret, IReadOnlyList<string> RecoveryCodes);

    /// <summary>The <c>secret</c> of a setup response.</summary>
    internal static async Task<string> SetupSecretAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await JsonAsync(response, ct);

        return body.GetProperty("secret").GetString()!;
    }

    /// <summary>The <c>otpauthUri</c> of a setup response.</summary>
    internal static async Task<string> OtpauthUriAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await JsonAsync(response, ct);

        return body.GetProperty("otpauthUri").GetString()!;
    }

    /// <summary>The <c>recoveryCodes</c> array of a verify or regenerate response.</summary>
    internal static async Task<IReadOnlyList<string>> RecoveryCodesAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var body = await JsonAsync(response, ct);

        return
        [
            .. body.GetProperty("recoveryCodes").EnumerateArray().Select(code => code.GetString()!),
        ];
    }

    /// <summary>The response body as JSON.</summary>
    internal static async Task<JsonElement> JsonAsync(HttpResponseMessage response, CancellationToken ct) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(ct));

    /// <summary>One scalar text from a query against the class database.</summary>
    internal static async Task<string?> ScalarAsync(TestDatabase database, string sql)
    {
        var values = await database.QueryStringsAsync(sql);

        return values.Count > 0 ? values[0] : null;
    }

    /// <summary>The <c>Retry-After</c> header of a 429, or null when the response carries none.</summary>
    internal static string? RetryAfter(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Retry-After", out var values) ? values.FirstOrDefault() : null;
}
