using System.Security.Cryptography;
using System.Text;
using NightTales.Api.IntegrationTests.Persistence;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Auth;

/// <summary>
/// The <c>user_action_tokens</c> store of 09.3.3-T1 (Tests table, 09 README BD3/WF11) against a real
/// PostgreSQL 17: the invalidating issue, the hash-only storage and the replay-proof consume.
/// </summary>
/// <remarks>
/// The store is resolved from a real Api host's scope and driven through the same <c>IUnitOfWork</c> the
/// Mediator pipeline uses, so the statements run in the shape production runs them (09 README D5).
/// </remarks>
public sealed class UserActionTokenTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The number of random bytes BD3 fixes for a token (256 bits).</summary>
    private const int TokenBytes = 32;

    [Fact]
    public async Task Issue_StoresHashOnly_AndReturnsPlaintextOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await Database.CreateUserBuilder().CreateAsync(ct: ct);

        var issued = await AuthTestFlow.IssueActionTokenAsync(
            factory.Services,
            user.Id,
            UserActionKind.PasswordReset,
            ct);

        issued.IsError.ShouldBeFalse(string.Join(", ", issued.Errors.Select(error => error.Code)));

        var token = issued.Value;
        token.Length.ShouldBe(Base64UrlLength(TokenBytes));
        token.ShouldNotContain("=", Case.Sensitive, "BD3's encoding is unpadded base64url");
        token.ShouldNotContain("+", Case.Sensitive);
        token.ShouldNotContain("/", Case.Sensitive);

        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        SqlProbe
            .ScalarText(Database, $"SELECT token_hash FROM user_action_tokens WHERE user_id = '{user.Id}'")
            .ShouldBe(expectedHash, "only the SHA-256 hex of the token is stored");

        // The plaintext is nowhere in the table (it would also collide with the unique hash index).
        SqlProbe
            .Count(Database, $"SELECT count(*) FROM user_action_tokens WHERE token_hash = '{token}'")
            .ShouldBe(0, "the plaintext token never reaches the database");

        SqlProbe
            .ScalarText(Database, $"SELECT kind FROM user_action_tokens WHERE user_id = '{user.Id}'")
            .ShouldBe("password_reset", "the column stores the enum's snake_case wire value");
    }

    [Fact]
    public async Task Issue_InvalidatesPreviousTokenOfSameKind()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await Database.CreateUserBuilder().CreateAsync(ct: ct);

        var first = await AuthTestFlow.IssueActionTokenAsync(factory.Services, user.Id, UserActionKind.PasswordReset, ct);
        var otherKind = await AuthTestFlow.IssueActionTokenAsync(factory.Services, user.Id, UserActionKind.EmailVerify, ct);
        var second = await AuthTestFlow.IssueActionTokenAsync(factory.Services, user.Id, UserActionKind.PasswordReset, ct);

        first.IsError.ShouldBeFalse();
        otherKind.IsError.ShouldBeFalse();
        second.IsError.ShouldBeFalse();

        LiveCount(user.Id, "password_reset").ShouldBe(1, "at most one live token per (user, kind)");
        LiveCount(user.Id, "email_verify").ShouldBe(1, "another kind's live token is untouched");

        LiveByHash(user.Id, "password_reset", Hash(first.Value)).ShouldBe(0, "the first token was invalidated by the second");
        LiveByHash(user.Id, "password_reset", Hash(second.Value)).ShouldBe(1);

        // A third one invalidates the second, so "forgot again" always kills the older mail.
        var third = await AuthTestFlow.IssueActionTokenAsync(factory.Services, user.Id, UserActionKind.PasswordReset, ct);
        third.IsError.ShouldBeFalse();
        LiveCount(user.Id, "password_reset").ShouldBe(1);
        LiveByHash(user.Id, "password_reset", Hash(second.Value)).ShouldBe(0);
    }

    [Fact]
    public async Task Issue_SubjectScoped_InvalidatesOnlyTheSameSubject()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await Database.CreateUserBuilder().CreateAsync(ct: ct);
        var subjectA = Guid.CreateVersion7();
        var subjectB = Guid.CreateVersion7();
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);

        var firstA = await AuthTestFlow.IssueSubjectActionTokenAsync(
            factory.Services, user.Id, UserActionKind.PublicationCancel, subjectA, expiresAt, ct);
        var secondA = await AuthTestFlow.IssueSubjectActionTokenAsync(
            factory.Services, user.Id, UserActionKind.PublicationCancel, subjectA, expiresAt, ct);
        var onlyB = await AuthTestFlow.IssueSubjectActionTokenAsync(
            factory.Services, user.Id, UserActionKind.PublicationCancel, subjectB, expiresAt, ct);

        firstA.IsError.ShouldBeFalse();
        secondA.IsError.ShouldBeFalse();
        onlyB.IsError.ShouldBeFalse();

        LiveByHash(user.Id, "publication_cancel", Hash(firstA.Value), subjectA)
            .ShouldBe(0, "the same subject's older token is invalidated");
        LiveByHash(user.Id, "publication_cancel", Hash(onlyB.Value), subjectB)
            .ShouldBe(1, "another subject's live token is untouched (06 CR-6/YR8)");
    }

    [Fact]
    public async Task Consume_IsSingleUse_UnderConcurrency()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await Database.CreateUserBuilder().CreateAsync(ct: ct);

        var issued = await AuthTestFlow.IssueActionTokenAsync(factory.Services, user.Id, UserActionKind.PasswordReset, ct);
        issued.IsError.ShouldBeFalse();

        var results = await Task.WhenAll(
            AuthTestFlow.ConsumeActionTokenAsync(factory.Services, issued.Value, UserActionKind.PasswordReset, ct),
            AuthTestFlow.ConsumeActionTokenAsync(factory.Services, issued.Value, UserActionKind.PasswordReset, ct));

        results.Count(result => !result.IsError).ShouldBe(1, "the UPDATE … RETURNING serialises the two callers");
        results.Count(result => result.IsError).ShouldBe(1);
        results.Single(result => !result.IsError).Value.ShouldBe(user.Id);
        results
            .Single(result => result.IsError)
            .Errors.ShouldContain(error => error.Code == ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Consume_WrongKind_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await Database.CreateUserBuilder().CreateAsync(ct: ct);

        var issued = await AuthTestFlow.IssueActionTokenAsync(factory.Services, user.Id, UserActionKind.EmailVerify, ct);
        issued.IsError.ShouldBeFalse();

        var consumed = await AuthTestFlow.ConsumeActionTokenAsync(
            factory.Services, issued.Value, UserActionKind.PasswordReset, ct);

        consumed.IsError.ShouldBeTrue("an email_verify token cannot reset a password");
        consumed.Errors.ShouldContain(error => error.Code == ErrorCodes.ValidationFailed);

        // The rejected consume wrote nothing: the token is still live and still spendable as its own kind.
        LiveCount(user.Id, "email_verify").ShouldBe(1);
        var correct = await AuthTestFlow.ConsumeActionTokenAsync(
            factory.Services, issued.Value, UserActionKind.EmailVerify, ct);
        correct.IsError.ShouldBeFalse();
        correct.Value.ShouldBe(user.Id);
    }

    [Fact]
    public async Task Consume_ExpiredToken_IsRejected_AndStaysUnused()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        using var factory = new ApiFactory(Database, clock);
        var user = await Database.CreateUserBuilder().CreateAsync(ct: ct);

        var issued = await AuthTestFlow.IssueActionTokenAsync(factory.Services, user.Id, UserActionKind.PasswordReset, ct);
        issued.IsError.ShouldBeFalse();

        // BD3: the password-reset lifetime is 60 minutes; 61 minutes later the token is dead.
        clock.Advance(TimeSpan.FromMinutes(61));

        var consumed = await AuthTestFlow.ConsumeActionTokenAsync(
            factory.Services, issued.Value, UserActionKind.PasswordReset, ct);

        consumed.IsError.ShouldBeTrue();
        consumed.Errors.ShouldContain(error => error.Code == ErrorCodes.ValidationFailed);
        SqlProbe
            .Count(Database, $"SELECT count(*) FROM user_action_tokens WHERE user_id = '{user.Id}' AND used_at IS NULL")
            .ShouldBe(1, "a rejected consume never marks the row used");
    }

    /// <summary>The stored form of a token: SHA-256 as lowercase hex (BD3).</summary>
    private static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>The length of <paramref name="bytes"/> in unpadded base64url.</summary>
    private static int Base64UrlLength(int bytes) =>
        ((bytes + 2) / 3 * 4) - (bytes % 3) switch { 1 => 2, 2 => 1, _ => 0 };

    private long LiveCount(Guid userId, string kind) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM user_action_tokens WHERE user_id = '{userId}' AND kind = '{kind}' AND used_at IS NULL");

    private long LiveByHash(Guid userId, string kind, string hash, Guid? subjectId = null) =>
        SqlProbe.Count(
            Database,
            $"SELECT count(*) FROM user_action_tokens WHERE user_id = '{userId}' AND kind = '{kind}'"
            + $" AND used_at IS NULL AND token_hash = '{hash}'"
            + (subjectId is { } subject ? $" AND subject_id = '{subject}'" : string.Empty));
}
