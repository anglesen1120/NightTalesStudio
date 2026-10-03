using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Research.Sources;
using NightTales.Application.Features.Research.Sources.Commands.AddSource;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;
using static NightTales.Infrastructure.UnitTests.Research.SourceHandlerHarness;

namespace NightTales.Infrastructure.UnitTests.Research;

/// <summary>
/// <c>AddSourceHandler</c> against real PostgreSQL (02.5.1 T1 Design: the order of the checks is the contract). Each test owns a
/// fresh user, series and episode because the class database is shared. The handler runs inside one transaction like the
/// pipeline's unit of work, and the tests commit explicitly.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AddSourceHandlerTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ErrorOr<SourceDto>> AddAsync(
        Scope scope,
        string? url,
        string? note = null,
        DateTimeOffset? now = null,
        SourcesOptions? options = null,
        Guid? asUser = null,
        bool commit = true)
    {
        var userId = asUser ?? scope.UserId;
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = userId });
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);

        var result = await AddHandler(db, userId, now, options).Handle(new AddSourceCommand(scope.EpisodeId, url, note), Ct);
        if (commit && !result.IsError)
        {
            await db.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
        }

        return result;
    }

    [Fact]
    public void SourceErrors_Codes_MatchTheErrorCatalogue()
    {
        SourceErrors.SourceUrlInvalidCode.ShouldBe(ErrorCodes.SourceUrlInvalid);
        SourceErrors.SourceDomainNotAllowedCode.ShouldBe(ErrorCodes.SourceDomainNotAllowed);
        Episode.InvalidStateCode.ShouldBe(ErrorCodes.EpisodeInvalidState);
    }

    [Fact]
    public async Task Add_Url_CreatesPendingSourceAndQueuedTask()
    {
        var scope = await NewEpisodeAsync(Database, ct: Ct);

        var result = await AddAsync(scope, "https://vnexpress.net/a");

        result.IsError.ShouldBeFalse();
        result.Value.Status.ShouldBe(StorySourceStatus.Pending);
        result.Value.Kind.ShouldBe(StorySourceKind.Url);
        (await CountSourcesAsync(Database, scope, Ct)).ShouldBe(1);
        (await CountTasksAsync(Database, scope, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Add_Url_AppendsOneCreationRecord_AndANoteAppendsNone()
    {
        var scope = await NewEpisodeAsync(Database, ct: Ct);

        (await AddAsync(scope, "https://vnexpress.net/a")).IsError.ShouldBeFalse();
        (await AddAsync(scope, null, "my own note")).IsError.ShouldBeFalse();

        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = scope.UserId });
        (await db.SourceCreations.CountAsync(creation => creation.UserId == scope.UserId, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Add_BlockedDomain_ReturnsSourceDomainNotAllowed()
    {
        var scope = await NewEpisodeAsync(Database, ct: Ct);

        var result = await AddAsync(scope, "https://m.youtube.com/watch?v=x");

        result.FirstError.Code.ShouldBe(ErrorCodes.SourceDomainNotAllowed);
        result.FirstError.Description.ShouldBe(SourceErrors.DomainNotAllowedMessage);
        (await CountSourcesAsync(Database, scope, Ct)).ShouldBe(0, "a rejected URL is never persisted");
    }

    [Theory]
    [InlineData("http://2130706433/")]
    [InlineData("https://user:pw@example.com/")]
    public async Task Add_IpLiteralUrl_ReturnsSourceUrlInvalid(string url)
    {
        var scope = await NewEpisodeAsync(Database, ct: Ct);

        (await AddAsync(scope, url)).FirstError.Code.ShouldBe(ErrorCodes.SourceUrlInvalid);
    }

    [Fact]
    public async Task Add_EleventhSource_ReturnsSourceLimitReached()
    {
        var scope = await NewEpisodeAsync(Database, ct: Ct);
        for (var index = 0; index < 10; index++)
        {
            await SeedUrlAsync(Database, scope.UserId, scope.EpisodeId, $"https://example.com/{index}", Now.AddHours(-1), ct: Ct);
        }

        var result = await AddAsync(scope, "https://example.com/new");

        result.FirstError.Code.ShouldBe(ErrorCodes.SourceLimitReached);
    }

    [Fact]
    public async Task Add_NoteSource_CountsTowardTheEpisodeCap()
    {
        var scope = await NewEpisodeAsync(Database, ct: Ct);
        for (var index = 0; index < 10; index++)
        {
            (await AddAsync(scope, null, $"note {index}")).IsError.ShouldBeFalse();
        }

        (await AddAsync(scope, null, "one too many")).FirstError.Code.ShouldBe(ErrorCodes.SourceLimitReached);
    }

    [Fact]
    public async Task Add_ThirtyFirstUrlSourceIn24h_Returns429WithRetryAfter()
    {
        var scope = await NewEpisodeAsync(Database, ct: Ct);
        var other = new[] { await NewEpisodeOfAsync(Database, scope, Ct), await NewEpisodeOfAsync(Database, scope, Ct), await NewEpisodeOfAsync(Database, scope, Ct) };
        for (var index = 0; index < 30; index++)
        {
            // The oldest (index 0) was created 20 h ago, so it leaves the window in 4 h.
            await SeedUrlAsync(Database, scope.UserId, other[index / 10], $"https://example.com/{index}", Now.AddHours(-20).AddMinutes(index), ct: Ct);
        }

        var result = await AddAsync(scope, "https://example.com/new");

        result.FirstError.Code.ShouldBe(ErrorCodes.SourceFetchLimitReached);
        result.FirstError.Metadata![ErrorMetadata.RetryAfterSeconds].ShouldBe(4 * 3600);
    }

    [Fact]
    public async Task Add_UrlSourcesOlderThan24h_AreNotCounted()
    {
        var scope = await NewEpisodeAsync(Database, ct: Ct);
        var other = new[] { await NewEpisodeOfAsync(Database, scope, Ct), await NewEpisodeOfAsync(Database, scope, Ct), await NewEpisodeOfAsync(Database, scope, Ct) };
        for (var index = 0; index < 30; index++)
        {
            await SeedUrlAsync(Database, scope.UserId, other[index / 10], $"https://example.com/{index}", Now.AddHours(-24), ct: Ct);
        }

        (await AddAsync(scope, "https://example.com/new")).IsError.ShouldBeFalse("created_at = now - 24 h has left the window");
    }

    [Fact]
    public async Task Add_NoteSource_DoesNotCountTowardUrlCap_AndCreatesNoFetchTask()
    {
        var scope = await NewEpisodeAsync(Database, ct: Ct);
        var other = new[] { await NewEpisodeOfAsync(Database, scope, Ct), await NewEpisodeOfAsync(Database, scope, Ct), await NewEpisodeOfAsync(Database, scope, Ct) };
        for (var index = 0; index < 30; index++)
        {
            await SeedUrlAsync(Database, scope.UserId, other[index / 10], $"https://example.com/{index}", Now.AddHours(-1), ct: Ct);
        }

        var result = await AddAsync(scope, null, "  I watched the documentary  ");

        result.IsError.ShouldBeFalse("notes cost nothing");
        result.Value.Kind.ShouldBe(StorySourceKind.UserNote);
        result.Value.Status.ShouldBe(StorySourceStatus.Fetched);
        result.Value.NoteText.ShouldBe("I watched the documentary");
        result.Value.ContentSha256.ShouldNotBeNull();
        result.Value.TextChars.ShouldBe("I watched the documentary".Length);
        (await CountTasksAsync(Database, scope, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Add_DuplicateNormalisedUrl_Returns409()
    {
        var scope = await NewEpisodeAsync(Database, ct: Ct);
        (await AddAsync(scope, "https://example.com/a")).IsError.ShouldBeFalse();

        foreach (var variant in new[] { "https://example.com/a", "https://EXAMPLE.com:443/a#frag", "https://example.com./a" })
        {
            (await AddAsync(scope, variant)).FirstError.Code.ShouldBe(ErrorCodes.SourceDuplicate, variant);
        }

        (await AddAsync(scope, "https://example.com/a?x=1")).IsError.ShouldBeFalse("the query is part of the identity");
    }

    [Fact]
    public async Task Add_WhileStoryApproved_ReturnsEpisodeInvalidState()
    {
        var scope = await NewEpisodeAsync(Database, EpisodeStatus.StoryApproved, Ct);

        (await AddAsync(scope, "https://example.com/a")).FirstError.Code.ShouldBe(ErrorCodes.EpisodeInvalidState);
    }

    [Fact]
    public async Task Add_InStoryReview_IsAccepted()
    {
        var scope = await NewEpisodeAsync(Database, EpisodeStatus.StoryReview, Ct);

        (await AddAsync(scope, "https://example.com/a")).IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task Add_OtherUsersEpisode_ReturnsNotFound()
    {
        var owner = await NewEpisodeAsync(Database, ct: Ct);
        var stranger = await NewEpisodeAsync(Database, ct: Ct);

        var result = await AddAsync(owner, "https://example.com/a", asUser: stranger.UserId);

        result.FirstError.Code.ShouldBe(ErrorCodes.NotFound);
        (await CountSourcesAsync(Database, owner, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Add_CreatesSourceAndTaskInOneTransaction()
    {
        var scope = await NewEpisodeAsync(Database, ct: Ct);
        await using var db = Database.CreateAppDbContext(new TestCurrentUser { Id = scope.UserId });
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);

        (await AddHandler(db, scope.UserId).Handle(new AddSourceCommand(scope.EpisodeId, "https://example.com/a", null), Ct)).IsError.ShouldBeFalse();
        await db.SaveChangesAsync(Ct);

        // Roll the unit of work back, as a failing second insert would: neither row may survive.
        await transaction.RollbackAsync(Ct);

        (await CountSourcesAsync(Database, scope, Ct)).ShouldBe(0);
        (await CountTasksAsync(Database, scope, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Add_Validator_RequiresExactlyOneOfUrlAndNote()
    {
        var validator = new AddSourceValidator(new SourcesOptions());
        var episode = Guid.NewGuid();

        (await validator.ValidateAsync(new AddSourceCommand(episode, null, null), Ct)).IsValid.ShouldBeFalse();
        (await validator.ValidateAsync(new AddSourceCommand(episode, "https://example.com", "note"), Ct)).IsValid.ShouldBeFalse();
        (await validator.ValidateAsync(new AddSourceCommand(episode, null, "   "), Ct)).IsValid.ShouldBeFalse("a whitespace note is a validation error");
        (await validator.ValidateAsync(new AddSourceCommand(episode, "https://example.com/" + new string('a', 2_100), null), Ct)).IsValid.ShouldBeFalse();
        (await validator.ValidateAsync(new AddSourceCommand(episode, null, new string('n', 3_001)), Ct)).IsValid.ShouldBeFalse();
        (await validator.ValidateAsync(new AddSourceCommand(episode, "https://example.com", null), Ct)).IsValid.ShouldBeTrue();
        (await validator.ValidateAsync(new AddSourceCommand(episode, null, "a note"), Ct)).IsValid.ShouldBeTrue();
    }
}
