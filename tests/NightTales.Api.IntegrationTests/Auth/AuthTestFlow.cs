using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ErrorOr;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.IntegrationTests.Http;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Security;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Entities;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using Shouldly;

namespace NightTales.Api.IntegrationTests.Auth;

/// <summary>
/// The shared plumbing of 09.3.2-T1's auth tests: real Identity users, the raw cookie headers the
/// browser would send, the problem <c>code</c> of a refusal, and the rows of <c>refresh_tokens</c>.
/// </summary>
/// <remarks>
/// <para>
/// Cookies are handled by hand rather than through <see cref="HttpClient"/>'s container on purpose: the
/// refresh cookie is <c>Secure</c>, and the in-memory test server is reached over plain HTTP, so a
/// container would drop it — and the cookie's flags are part of the contract these tests assert anyway.
/// </para>
/// <para>
/// The database is read through the write model (<see cref="AppDbContext.RefreshTokens"/>) for state
/// assertions: <c>refresh_tokens</c> is not tenant-filtered, and going through the model proves the
/// handler's writes really committed instead of leaving a transaction open.
/// </para>
/// </remarks>
internal static class AuthTestFlow
{
    /// <summary>A BD2-valid password (12 characters, no composition rules).</summary>
    internal const string Password = "Abcdefghijkl";

    internal const string LoginPath = "/api/v1/auth/login";

    internal const string RefreshPath = "/api/v1/auth/refresh";

    internal const string LogoutPath = "/api/v1/auth/logout";

    /// <summary>The configured <c>Auth:Refresh:CookieName</c> default (BD3).</summary>
    internal const string CookieName = "__Secure-nts_rt";

    /// <summary>The cookie's <c>Path</c>: the auth group, so no other route receives it (BD3).</summary>
    internal const string RefreshCookiePath = "/api/v1/auth";

    /// <summary>The one origin the CSRF filter accepts; the factory configures it as <c>App:PublicOrigin</c>.</summary>
    internal const string Origin = ApiFactory.PublicOrigin;

    /// <summary>The request header the CSRF filter reads.</summary>
    internal const string OriginHeaderName = "Origin";

    /// <summary>Logs in and returns the response together with the refresh cookie pair it set.</summary>
    internal static async Task<(HttpResponseMessage Response, string? Cookie)> LoginAsync(
        HttpClient client,
        string email,
        string password,
        CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync(LoginPath, new { email, password }, ct);

        return (response, CookiePair(response));
    }

    /// <summary>Refreshes with <paramref name="cookie"/> and the given <c>Origin</c> (null to omit it).</summary>
    internal static async Task<(HttpResponseMessage Response, string? Cookie)> RefreshAsync(
        HttpClient client,
        string cookie,
        string? origin,
        CancellationToken ct)
    {
        using var request = CookieRequest(HttpMethod.Post, RefreshPath, cookie, origin);

        var response = await client.SendAsync(request, ct);

        return (response, CookiePair(response));
    }

    /// <summary>Logs out with <paramref name="cookie"/> and the given <c>Origin</c>.</summary>
    internal static Task<HttpResponseMessage> LogoutAsync(
        HttpClient client,
        string cookie,
        string? origin,
        CancellationToken ct) =>
        client.SendAsync(CookieRequest(HttpMethod.Post, LogoutPath, cookie, origin), ct);

    /// <summary>A POST carrying the refresh cookie, and the <c>Origin</c> header when one is given.</summary>
    internal static HttpRequestMessage CookieRequest(
        HttpMethod method,
        string path,
        string? cookie,
        string? origin)
    {
        var request = new HttpRequestMessage(method, path);

        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        if (origin is not null)
        {
            request.Headers.Add(OriginHeaderName, origin);
        }

        return request;
    }

    /// <summary>The <c>code</c> of a problem response, or null when the body carries none.</summary>
    internal static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        return string.IsNullOrWhiteSpace(body)
            ? null
            : JsonSerializer.Deserialize<ProblemResponse>(body, ProblemResponse.Options)?.Code;
    }

    /// <summary>The <c>name=value</c> of the refresh cookie a response set, or null when it set none.</summary>
    internal static string? CookiePair(HttpResponseMessage response)
    {
        var header = SetCookie(response);

        return header?[..header.IndexOf(';', StringComparison.Ordinal)];
    }

    /// <summary>The whole <c>Set-Cookie</c> value of the refresh cookie, flags included.</summary>
    internal static string? SetCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(value => value.StartsWith(CookieName + "=", StringComparison.Ordinal))
            : null;

    /// <summary>The stored form of a refresh token: SHA-256 as lowercase hex (BD3).</summary>
    internal static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>The cookie pair's value, without the cookie name.</summary>
    internal static string CookieValue(string cookiePair)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cookiePair);

        return cookiePair[(CookieName.Length + 1)..];
    }

    /// <summary>The <c>accessToken</c> of a completed login or refresh body.</summary>
    internal static async Task<string> AccessTokenAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(ct));

        return body.GetProperty("accessToken").GetString()!;
    }

    /// <summary>Creates a real Identity user with a password, through the Identity store.</summary>
    internal static async Task<AppUser> CreateUserAsync(
        IServiceProvider services,
        PlatformRole role = PlatformRole.User,
        bool emailConfirmed = true,
        bool twoFactorEnabled = false,
        UserStatus status = UserStatus.Active)
    {
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var email = $"auth-{Guid.CreateVersion7():N}@nighttales.test";
        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = emailConfirmed,
            TwoFactorEnabled = twoFactorEnabled,
            DisplayName = "auth test",
            Role = role,
            Status = status,
        };

        (await users.CreateAsync(user, Password)).Succeeded.ShouldBeTrue(
            "the fixture user must be creatable with the BD2 password policy");

        return user;
    }

    /// <summary>Applies <paramref name="change"/> to a user through the write model.</summary>
    internal static async Task UpdateUserAsync(IServiceProvider services, Guid userId, Action<AppUser> change)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await context.Users.SingleAsync(candidate => candidate.Id == userId);

        change(user);
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Every <c>refresh_tokens</c> row of one user, oldest first. Rows minted at the same
    /// <see cref="FakeClock"/> instant tie on <c>created_at</c>, so ties are broken by rotation depth
    /// (a successor sorts after the row whose <c>replaced_by_id</c> names it), then by id.
    /// </summary>
    internal static async Task<IReadOnlyList<RefreshToken>> RowsAsync(TestDatabase database, Guid userId)
    {
        await using var context = database.CreateAppDbContext();

        var rows = await context.RefreshTokens
            .AsNoTracking()
            .Where(row => row.UserId == userId)
            .ToListAsync();

        var predecessorOf = rows
            .Where(row => row.ReplacedById is not null)
            .ToDictionary(row => row.ReplacedById!.Value);

        int Depth(RefreshToken row)
        {
            var depth = 0;
            while (predecessorOf.TryGetValue(row.Id, out var predecessor) && depth < rows.Count)
            {
                row = predecessor;
                depth++;
            }

            return depth;
        }

        return [.. rows.OrderBy(row => row.CreatedAt).ThenBy(Depth).ThenBy(row => row.Id)];
    }

    /// <summary>
    /// Writes one family's first row directly, so a session-limit rule can be exercised without logging in.
    /// The row is created at <paramref name="createdAt"/> and, like every root row, last used then (WT33):
    /// an idle-window test therefore passes a <paramref name="createdAt"/> older than the window, not a
    /// future <c>last_used_at</c>.
    /// </summary>
    /// <remarks>
    /// 09.3.2-T1 answers 401 <c>mfa_required</c>/<c>mfa_enrolment_required</c> for an owner or admin, and
    /// 09.3.2-T3 is the task that completes that login (WT22/WT39). Until then an admin session cannot be
    /// created through the API at all, so the admin-limit tests seed the row the login would have written —
    /// exactly what <c>EfRefreshTokenService.StartSessionAsync</c> writes.
    /// </remarks>
    internal static async Task<RefreshToken> SeedFamilyAsync(
        TestDatabase database,
        AppUser user,
        string token,
        DateTimeOffset createdAt,
        TimeSpan absolute)
    {
        await using var context = database.CreateAppDbContext();

        var row = RefreshToken.Start(
            user.Id,
            Hash(token),
            createdAt,
            createdAt + absolute,
            ip: null,
            userAgent: null);

        context.RefreshTokens.Add(row);
        await context.SaveChangesAsync();

        return row;
    }

    /// <summary>One scalar string from a query, or null.</summary>
    internal static async Task<string?> ScalarAsync(TestDatabase database, string sql)
    {
        var values = await database.QueryStringsAsync(sql);

        return values.Count > 0 ? values[0] : null;
    }

    /// <summary>
    /// Issues one single-use action token of <paramref name="kind"/> through the real store, inside a
    /// unit of work, exactly as the send-time minter or a handler does (09.3.3-T1).
    /// </summary>
    /// <param name="services">The started host's service provider.</param>
    /// <param name="userId">The user the token authorizes.</param>
    /// <param name="kind">The flow the token belongs to.</param>
    /// <param name="ct">Cancels the issue.</param>
    internal static async Task<ErrorOr<string>> IssueActionTokenAsync(
        IServiceProvider services,
        Guid userId,
        UserActionKind kind,
        CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginAsync(ct);

        var issued = await scope
            .ServiceProvider
            .GetRequiredService<IUserActionTokenService>()
            .IssueAsync(userId, kind, new RequestInfo(null, null), ct);

        if (issued.IsError)
        {
            await unitOfWork.RollbackAsync(ct);
            return issued;
        }

        (await unitOfWork.CommitAsync(ct)).IsError.ShouldBeFalse("the issue commits with its caller's unit of work");

        return issued;
    }

    /// <summary>The subject-scoped issue of 06 CR-6/YR8, driven the same way.</summary>
    /// <param name="services">The started host's service provider.</param>
    /// <param name="userId">The user the token authorizes.</param>
    /// <param name="kind">The subject-scoped flow.</param>
    /// <param name="subjectId">The row the token is about.</param>
    /// <param name="expiresAt">The explicit expiry the flow fixes.</param>
    /// <param name="ct">Cancels the issue.</param>
    internal static async Task<ErrorOr<string>> IssueSubjectActionTokenAsync(
        IServiceProvider services,
        Guid userId,
        UserActionKind kind,
        Guid subjectId,
        DateTimeOffset expiresAt,
        CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginAsync(ct);

        var issued = await scope
            .ServiceProvider
            .GetRequiredService<IUserActionTokenService>()
            .IssueAsync(userId, kind, subjectId, expiresAt, new RequestInfo(null, null), ct);

        if (issued.IsError)
        {
            await unitOfWork.RollbackAsync(ct);
            return issued;
        }

        (await unitOfWork.CommitAsync(ct)).IsError.ShouldBeFalse();

        return issued;
    }

    /// <summary>
    /// One consume in its own scope; the store's statement is self-atomic, so no transaction is opened
    /// (09.3.3-T1: the replay-proof <c>UPDATE … RETURNING</c> is the whole protocol).
    /// </summary>
    /// <param name="services">The started host's service provider.</param>
    /// <param name="token">The presented plaintext token.</param>
    /// <param name="kind">The flow the caller is performing.</param>
    /// <param name="ct">Cancels the statement.</param>
    internal static async Task<ErrorOr<Guid>> ConsumeActionTokenAsync(
        IServiceProvider services,
        string token,
        UserActionKind kind,
        CancellationToken ct)
    {
        using var scope = services.CreateScope();

        return await scope
            .ServiceProvider
            .GetRequiredService<IUserActionTokenService>()
            .ConsumeAsync(token, kind, ct);
    }
}
