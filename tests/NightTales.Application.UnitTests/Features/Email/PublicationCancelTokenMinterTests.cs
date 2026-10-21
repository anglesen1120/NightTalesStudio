using ErrorOr;
using NightTales.Application.Abstractions.Email;
using NightTales.Application.Abstractions.Security;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Email;
using NightTales.Application.Features.Email.TokenMinters;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Email;

/// <summary>
/// The send-time minter of 06.2.2-T2 (06 README D7, YR8; 09 README §B10 WT17/WT30): the cancel link exists only
/// when the veto notice is actually sent, its lifetime is <c>min(vetoEndsAt, now + 48 h)</c>, and the link puts
/// the single-use token after the <c>#</c>.
/// </summary>
/// <remarks>
/// The fakes are nested here on purpose: the token store records what was issued (id, kind, subject, expiry) so a
/// test can prove that the row is created only at send time and that the queued parameters never carry the token.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class PublicationCancelTokenMinterTests
{
    private static readonly Guid UserId = Guid.Parse("018f3c3e-0000-7000-8000-0000000000aa");

    private static readonly Guid PublicationId = Guid.Parse("018f3c3e-7a2b-7c40-8e11-1a2b3c4d5e6f");

    /// <summary>An ISO-8601 deadline 24 h after <see cref="FakeClock.DefaultStart"/>: shorter than the 48 h cap.</summary>
    private const string VetoEndsAtWithinCap = "2026-01-02T00:00:00.0000000+00:00";

    /// <summary>The same deadline as an instant, for the assertion on what the token service was told.</summary>
    private static readonly DateTimeOffset VetoEndsAtWithinCapAt = new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NoTokenBeforeSendTime()
    {
        // The producer's queued values: the veto notice's producer keys, which by construction exclude the
        // minted `cancelUrl` — the shape 09.2.6-T3's own "no token before send" test pins for `password_reset`.
        var parameters = ProducerParameters();
        var tokens = new FakeTokenService();
        var minter = CreateMinter(tokens);

        var definition = VetoNoticeDefinition();

        definition.ProducerKeys.ShouldNotContain(PublicationCancelTokenMinter.CancelUrlKey);
        definition.MinterKeys.ShouldBe([PublicationCancelTokenMinter.CancelUrlKey], ignoreOrder: true);
        parameters.Values.ShouldAllBe(value => !value.Contains(FakeTokenService.Plaintext, StringComparison.Ordinal));

        // Nothing was minted for the queue step: no `user_action_tokens` row exists before the job runs.
        tokens.Issued.ShouldBeEmpty();

        var minted = await minter.MintAsync(
            UserId,
            EmailTemplateCatalog.PublicationVetoNotice,
            parameters,
            TestContext.Current.CancellationToken);

        minted.IsError.ShouldBeFalse();

        // The token exists only in the minter's answer — never in the queued parameters, which is the whole
        // point of the send-time split (WT17/WT30).
        minted.Value[PublicationCancelTokenMinter.CancelUrlKey].ShouldContain(FakeTokenService.Plaintext);
        parameters.Values.ShouldAllBe(value => !value.Contains(FakeTokenService.Plaintext, StringComparison.Ordinal));
        tokens.Issued.Count.ShouldBe(1);
        tokens.Issued[0].Kind.ShouldBe(UserActionKind.PublicationCancel);
        tokens.Issued[0].SubjectId.ShouldBe(PublicationId);
        tokens.Issued[0].UserId.ShouldBe(UserId);
    }

    [Fact]
    public async Task ReturnsCancelUrlWithFragment()
    {
        var minted = await CreateMinter(new FakeTokenService()).MintAsync(
            UserId,
            EmailTemplateCatalog.PublicationVetoNotice,
            ProducerParameters(),
            TestContext.Current.CancellationToken);

        minted.IsError.ShouldBeFalse();

        var cancelUrl = minted.Value[PublicationCancelTokenMinter.CancelUrlKey];

        cancelUrl.ShouldBe(
            "https://app.nighttales.app/publications/cancel#p=018f3c3e-7a2b-7c40-8e11-1a2b3c4d5e6f&t=" + FakeTokenService.Plaintext);

        // The contract, not the formatting: the token is inside the fragment a browser never sends.
        cancelUrl[(cancelUrl.IndexOf('#', StringComparison.Ordinal) + 1)..]
            .ShouldBe($"p={PublicationId:D}&t={FakeTokenService.Plaintext}");
        cancelUrl.ShouldNotContain("?t=");
    }

    [Fact]
    public async Task Expiry_IsTheVetoDeadline_WhenTheWindowIsShorterThanTheCap()
    {
        var tokens = new FakeTokenService();

        await CreateMinter(tokens).MintAsync(
            UserId,
            EmailTemplateCatalog.PublicationVetoNotice,
            ProducerParameters(),
            TestContext.Current.CancellationToken);

        tokens.Issued[0].ExpiresAt.ShouldBe(VetoEndsAtWithinCapAt);
    }

    [Fact]
    public async Task Expiry_IsCappedAtFortyEightHours_WhenTheWindowIsLonger()
    {
        var tokens = new FakeTokenService();
        var clock = new FakeClock();
        var parameters = ProducerParameters();
        parameters[PublicationCancelTokenMinter.VetoEndsAtParameter] = "2026-06-01T00:00:00.0000000+00:00";

        await CreateMinter(tokens, clock).MintAsync(
            UserId,
            EmailTemplateCatalog.PublicationVetoNotice,
            parameters,
            TestContext.Current.CancellationToken);

        tokens.Issued[0].ExpiresAt.ShouldBe(clock.UtcNow.Add(PublicationCancelTokenMinter.MaximumLifetime));
    }

    [Fact]
    public async Task ClosedVetoWindow_MintsNothing()
    {
        // A notice queued before the deadline but delivered after it: the link would be dead on arrival, so no
        // token row is created and the job has an error to record instead of a capability to mail.
        var tokens = new FakeTokenService();
        var parameters = ProducerParameters();
        parameters[PublicationCancelTokenMinter.VetoEndsAtParameter] = "2025-12-31T23:59:59.0000000+00:00";

        var minted = await CreateMinter(tokens).MintAsync(
            UserId,
            EmailTemplateCatalog.PublicationVetoNotice,
            parameters,
            TestContext.Current.CancellationToken);

        minted.IsError.ShouldBeTrue();
        minted.FirstError.Code.ShouldBe(ErrorCodes.EmailSendFailed);
        tokens.Issued.ShouldBeEmpty();
    }

    [Fact]
    public async Task OtherTemplate_MintsNothing()
    {
        var tokens = new FakeTokenService();

        // A minter is resolved by template name, so this answers the empty set rather than throwing: the catalog
        // decides which templates are token-bearing (EmailTokenMinters).
        var minted = await CreateMinter(tokens).MintAsync(
            UserId,
            EmailTemplateCatalog.PasswordReset,
            ProducerParameters(),
            TestContext.Current.CancellationToken);

        minted.IsError.ShouldBeFalse();
        minted.Value.ShouldBeEmpty();
        tokens.Issued.ShouldBeEmpty();
    }

    [Fact]
    public async Task MalformedParameters_ReturnAnError_AndNeverThrow()
    {
        var tokens = new FakeTokenService();
        var minter = CreateMinter(tokens);

        var badPublicationId = ProducerParameters();
        badPublicationId[PublicationCancelTokenMinter.PublicationIdParameter] = "not-a-guid";

        // `Guid.TryParse` accepts the all-zero id, but the token store refuses it as a subject (it can never be a
        // publication), so the minter treats it as malformed rather than letting that refusal escape as an
        // exception from behind the port.
        var emptyPublicationId = ProducerParameters();
        emptyPublicationId[PublicationCancelTokenMinter.PublicationIdParameter] = Guid.Empty.ToString("D");

        var badVetoEndsAt = ProducerParameters();
        badVetoEndsAt[PublicationCancelTokenMinter.VetoEndsAtParameter] = "tomorrow-ish";

        var missingPublicationId = ProducerParameters();
        missingPublicationId.Remove(PublicationCancelTokenMinter.PublicationIdParameter);

        var missingVetoEndsAt = ProducerParameters();
        missingVetoEndsAt.Remove(PublicationCancelTokenMinter.VetoEndsAtParameter);

        Dictionary<string, string>[] broken =
        [
            badPublicationId,
            emptyPublicationId,
            badVetoEndsAt,
            missingPublicationId,
            missingVetoEndsAt,
        ];

        foreach (var parameters in broken)
        {
            // An exception here would fail the test too, so what this pins is the error path: a parameter this job
            // cannot read is a failed attempt with the send-failure code, never an exception out of a parse.
            var minted = await minter.MintAsync(
                UserId,
                EmailTemplateCatalog.PublicationVetoNotice,
                parameters,
                TestContext.Current.CancellationToken);

            minted.IsError.ShouldBeTrue();
            minted.FirstError.Code.ShouldBe(ErrorCodes.EmailSendFailed);
            minted.FirstError.Description.ShouldNotContain("not-a-guid");
            minted.FirstError.Description.ShouldNotContain("tomorrow-ish");
        }

        tokens.Issued.ShouldBeEmpty();
    }

    private static PublicationCancelTokenMinter CreateMinter(FakeTokenService tokens, FakeClock? clock = null) =>
        new(tokens, new FakeLinkOrigin(), clock ?? new FakeClock());

    /// <summary>The veto notice's catalog entry; a missing one fails the test with a plain message.</summary>
    private static EmailTemplateDefinition VetoNoticeDefinition() =>
        EmailTemplateCatalog.Find(EmailTemplateCatalog.PublicationVetoNotice)
        ?? throw new InvalidOperationException(
            "The veto notice must stay a catalog entry to be queued and rendered (06.2.2-T2 §Design).");

    /// <summary>
    /// The queued values of one veto notice: exactly the catalog's producer keys, so a key the catalog does not
    /// allow — or a minted one — cannot slip in here unnoticed.
    /// </summary>
    private static Dictionary<string, string> ProducerParameters()
    {
        var production = VetoNoticeDefinition().ProducerKeys;

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["displayName"] = "An",
            ["channelTitle"] = "Late Night Tales",
            ["videoTitle"] = "The Lighthouse — Episode 1",
            ["description"] = "A quiet start on a loud coast.",
            ["tags"] = "horror, radio drama",
            ["privacy"] = "private",
            ["privacyNote"] = string.Empty,
            ["scheduledAt"] = string.Empty,
            ["vetoEndsAt"] = VetoEndsAtWithinCap,
            ["publicationUrl"] = "https://app.nighttales.app/publications/018f3c3e-7a2b-7c40-8e11-1a2b3c4d5e6f",
            [PublicationCancelTokenMinter.PublicationIdParameter] = PublicationId.ToString("D"),
        };

        values.Keys.ShouldBe(production, ignoreOrder: true);

        return values;
    }

    /// <summary>The token store, with what a minter must get right recorded.</summary>
    private sealed class FakeTokenService : IUserActionTokenService
    {
        /// <summary>The plaintext token every issue answers.</summary>
        internal const string Plaintext = "minted-link-value";

        /// <summary>Every subject-scoped issue, in order.</summary>
        internal List<(Guid UserId, UserActionKind Kind, Guid SubjectId, DateTimeOffset ExpiresAt)> Issued { get; } = [];

        public Task<ErrorOr<string>> IssueAsync(
            Guid userId,
            UserActionKind kind,
            RequestInfo info,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The cancel-link minter issues subject-scoped tokens only.");

        public Task<ErrorOr<string>> IssueAsync(
            Guid userId,
            UserActionKind kind,
            Guid subjectId,
            DateTimeOffset expiresAt,
            RequestInfo info,
            CancellationToken cancellationToken)
        {
            Issued.Add((userId, kind, subjectId, expiresAt));

            return Task.FromResult<ErrorOr<string>>(Plaintext);
        }

        public Task<ErrorOr<ConsumedActionToken>> ConsumeAsync(
            string token,
            UserActionKind kind,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The cancel-link minter never consumes a token.");
    }

    /// <summary>The origin every link is composed from, without a configuration source.</summary>
    private sealed class FakeLinkOrigin : IEmailLinkOrigin
    {
        public Uri Origin { get; } = new("https://app.nighttales.app");
    }
}
