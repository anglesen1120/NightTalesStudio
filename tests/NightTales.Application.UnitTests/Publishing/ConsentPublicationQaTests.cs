using System.Data.Common;
using System.Globalization;
using System.Reflection;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Common;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Publishing.Commands.ConsentPublication;
using NightTales.Application.Publishing;
using NightTales.Domain.Episodes;
using NightTales.Domain.Publishing;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Publishing;

/// <summary>
/// QA pass over <see cref="ConsentPublicationHandler"/> (06.2.2-T1, 06 README D7/D12/D13/YR3) on a real PostgreSQL
/// 17: the round-trip shape of the command (task §Acceptance "≤ 3 round trips, no N+1"), the seam the handler does
/// <em>not</em> carry (no scheduling port), and what a publication that vanishes between the read and the save
/// answers (task §Failure handling).
/// </summary>
/// <remarks>
/// Additive tests only: the task's own cases stay pinned by <see cref="ConsentPublicationHandlerTests"/> and the
/// Api-level <c>ConsentPublicationTests</c>. The handler is called the way the pipeline calls it — one
/// <see cref="AppDbContext"/> per scope, with the interceptor under test attached to that context's options.
/// </remarks>
/// <param name="database">The class database, injected by xUnit as the class fixture.</param>
[Trait("Category", "Integration")]
public sealed class ConsentPublicationQaTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>The frozen instant every test's clock starts at.</summary>
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The consent text version the host holds in these tests (06 README D11's default).</summary>
    private const string TextVersion = "pc.v1";

    [Fact]
    public async Task Consent_TwoReadsAndOneFlush_NoNPlusOne()
    {
        var scene = await NewSceneAsync();
        var interceptor = new CountingDbCommandInterceptor();
        var clock = new FakeClock(Now);

        // The options the production persistence layer builds, plus the counter: the handler's own context.
        var counted = new DbContextOptionsBuilder<AppDbContext>(
                TestDbContextFactory.AppOptions(Database.ConnectionString, clock))
            .AddInterceptors(interceptor)
            .Options;

        await using var context = new AppDbContext(counted, scene.CurrentUser);

        var command = new ConsentPublicationCommand(
            scene.PublicationId,
            TextVersion,
            await EtagAsync(scene));

        var handler = new ConsentPublicationHandler(
            context,
            new YouTubeOptions { ConsentTextVersion = TextVersion },
            clock,
            NullLogger<ConsentPublicationHandler>.Instance);

        var result = await handler.Handle(command, TestContext.Current.CancellationToken);

        result.IsError.ShouldBeFalse(result.IsError ? result.FirstError.Description : string.Empty);

        var reads = interceptor.Texts
            .Where(text => text.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            .ToList();

        reads.Count.ShouldBe(
            2,
            "the command reads the publication (with its connection) and the episode, nothing else: "
            + string.Join(" | ", interceptor.Texts));

        reads[0].ShouldContain("channel_connections", Case.Insensitive);
        reads[0].ShouldContain("publications", Case.Insensitive);
        reads[1].ShouldContain("episodes", Case.Insensitive);

        // Three round trips of the command: the two reads above and the one flush that writes the publication,
        // the episode and the outbox message inside a single transaction.
        interceptor.Count.ShouldBeLessThanOrEqualTo(
            5,
            "one flush is three statements in one transaction: " + string.Join(" | ", interceptor.Texts));
        interceptor.ObservedConcurrentExecution.ShouldBeFalse(
            "the reads stay sequential on the one context (WT46)");
    }

    [Fact]
    public void HandlerCarriesNoSchedulingPort()
    {
        // YR3: the consent path schedules nothing. The Api host registers a throwing IJobScheduler, so a call
        // would fail every Api test; this pins the seam itself — the handler cannot be handed a scheduler.
        var ports = typeof(ConsentPublicationHandler)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType.Name)
            .ToList();

        ports.ShouldNotContain(name => name.Contains("JobScheduler", StringComparison.Ordinal));
        ports.ShouldNotContain(name => name.Contains("PublishScheduler", StringComparison.Ordinal));
        ports.ShouldNotContain(name => name.Contains("EmailSender", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PublicationDeletedBetweenTheReadAndTheSave_Is404NotACrash()
    {
        var scene = await NewSceneAsync();
        var clock = new FakeClock(Now);
        var interceptor = new PublicationDeletingInterceptor(Database, scene.PublicationId);

        var counted = new DbContextOptionsBuilder<AppDbContext>(
                TestDbContextFactory.AppOptions(Database.ConnectionString, clock))
            .AddInterceptors(interceptor)
            .Options;

        await using var context = new AppDbContext(counted, scene.CurrentUser);

        var command = new ConsentPublicationCommand(
            scene.PublicationId,
            TextVersion,
            await EtagAsync(scene));

        var handler = new ConsentPublicationHandler(
            context,
            new YouTubeOptions { ConsentTextVersion = TextVersion },
            clock,
            NullLogger<ConsentPublicationHandler>.Instance);

        var result = await handler.Handle(command, TestContext.Current.CancellationToken);

        interceptor.Deleted.ShouldBe(1, "the probe must have removed the row before the flush");

        // 06.2.2-T1 §Failure handling: "a publication deleted between the read and the save: 404 `not_found`, never
        // a 500". The xmin token reports the vanished row as 0 rows affected, exactly like a lost update, so the
        // handler runs one cheap existence read and answers the same 404 its load path would answer.
        result.IsError.ShouldBeTrue("a publication that no longer exists cannot be consented");
        result.FirstError.Code.ShouldBe(ErrorCodes.NotFound);
        result.FirstError.Type.ShouldBe(ErrorType.NotFound);

        (await CountAsync($"SELECT count(*)::text FROM publications WHERE id = '{scene.PublicationId:D}'"))
            .ShouldBe(0);
        (await CountAsync(
            "SELECT count(*)::text FROM outbox_messages WHERE type = 'publication_updated.v1' "
            + $"AND payload::text LIKE '%{scene.PublicationId:D}%'"))
            .ShouldBe(0, "the failed flush rolls the outbox row back with it");
        (await ScalarAsync($"SELECT status FROM episodes WHERE id = '{scene.EpisodeId:D}'"))
            .ShouldBe("ready", "the episode transition never commits without the publication write");
    }

    [Fact]
    public async Task PublicationMovedBetweenTheReadAndTheSave_Is409ConcurrencyConflict()
    {
        var scene = await NewSceneAsync();
        var clock = new FakeClock(Now);
        var interceptor = new PublicationTouchingInterceptor(Database, scene.PublicationId);

        var counted = new DbContextOptionsBuilder<AppDbContext>(
                TestDbContextFactory.AppOptions(Database.ConnectionString, clock))
            .AddInterceptors(interceptor)
            .Options;

        await using var context = new AppDbContext(counted, scene.CurrentUser);

        var command = new ConsentPublicationCommand(
            scene.PublicationId,
            TextVersion,
            await EtagAsync(scene));

        var handler = new ConsentPublicationHandler(
            context,
            new YouTubeOptions { ConsentTextVersion = TextVersion },
            clock,
            NullLogger<ConsentPublicationHandler>.Instance);

        var result = await handler.Handle(command, TestContext.Current.CancellationToken);

        interceptor.Touched.ShouldBe(1, "the probe must have moved the row before the flush");

        // The row is still there but no longer names the tag the command compared: the documented 409 of a lost
        // update (09.2.4-T2), never the 404 that only a vanished row answers.
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.ConcurrencyConflict);
        result.FirstError.Type.ShouldBe(ErrorType.Conflict);

        (await CountAsync($"SELECT count(*)::text FROM publications WHERE id = '{scene.PublicationId:D}'"))
            .ShouldBe(1);
        (await ScalarAsync($"SELECT status FROM publications WHERE id = '{scene.PublicationId:D}'"))
            .ShouldBe("awaiting_consent", "the loser writes nothing of its own");
        (await CountAsync(
            "SELECT count(*)::text FROM outbox_messages WHERE type = 'publication_updated.v1' "
            + $"AND payload::text LIKE '%{scene.PublicationId:D}%'"))
            .ShouldBe(0, "the failed flush rolls the outbox row back with it");
        (await ScalarAsync($"SELECT status FROM episodes WHERE id = '{scene.EpisodeId:D}'"))
            .ShouldBe("ready", "the episode transition never commits without the publication write");
    }

    /// <summary>The row's current strong ETag (<c>"pu-&lt;xmin&gt;"</c>).</summary>
    private async Task<string> EtagAsync(Scene scene)
    {
        var xmin = uint.Parse(
            await ScalarAsync($"SELECT xmin::text FROM publications WHERE id = '{scene.PublicationId:D}'"),
            CultureInfo.InvariantCulture);

        return ETags.Publication(xmin);
    }

    /// <summary>One seeded owner: an episode in <c>ready</c> and its publication in <c>awaiting_consent</c>.</summary>
    private async Task<Scene> NewSceneAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = Guid.CreateVersion7();
        var currentUser = new TestCurrentUser { Id = userId };

        await new UserBuilder(Database, currentUser).CreateAsync(userId, ct: ct);

        var series = await new SeriesBuilder(Database, currentUser).CreateAsync(userId, ct: ct);

        var episode = await new EpisodeBuilder(Database, currentUser)
            .CreateAsync(userId, series.Id, status: EpisodeStatus.Ready, ct: ct);

        var connection = await new ChannelConnectionBuilder(Database, currentUser)
            .CreateAsync(userId, ChannelConnectionStatus.Active, ct: ct);

        var publicationId = await new PublicationBuilder(Database).CreateAsync(
            userId,
            episode.Id,
            connection.Id,
            PublicationStatus.AwaitingConsent,
            ct: ct);

        return new Scene(userId, currentUser, episode.Id, publicationId);
    }

    private async Task<string> ScalarAsync(string sql)
    {
        var rows = await Database.QueryStringsAsync(sql, TestContext.Current.CancellationToken);

        rows.Count.ShouldBe(1);

        return rows[0];
    }

    private async Task<int> CountAsync(string sql) =>
        int.Parse(await ScalarAsync(sql), CultureInfo.InvariantCulture);

    /// <summary>The rows of one scene, as the tests address them.</summary>
    private sealed record Scene(
        Guid UserId,
        TestCurrentUser CurrentUser,
        Guid EpisodeId,
        Guid PublicationId);

    /// <summary>
    /// Removes the publication the handler has already loaded, at the moment the handler reads its episode: the
    /// row is gone when the flush runs, which is the "deleted between the read and the save" case.
    /// </summary>
    private sealed class PublicationDeletingInterceptor(TestDatabase database, Guid publicationId)
        : DbCommandInterceptor
    {
        private int _deleted;

        /// <summary>How many rows the probe removed (1 at most).</summary>
        internal int Deleted => _deleted;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (command.CommandText.Contains("from episodes", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref _deleted, 1) == 0)
            {
                await database.ExecuteAsync(
                    $"DELETE FROM publications WHERE id = '{publicationId:D}'",
                    cancellationToken).ConfigureAwait(false);
            }

            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Moves the publication the handler has already loaded, at the moment the handler reads its episode: the row
    /// is still there but its <c>xmin</c> is no longer the one the command compared, which is the genuine concurrent
    /// change (a cancel or a second consent landing first).
    /// </summary>
    private sealed class PublicationTouchingInterceptor(TestDatabase database, Guid publicationId)
        : DbCommandInterceptor
    {
        private int _touched;

        /// <summary>How many probes moved the row (1 at most).</summary>
        internal int Touched => _touched;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (command.CommandText.Contains("from episodes", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref _touched, 1) == 0)
            {
                await database.ExecuteAsync(
                    $"UPDATE publications SET updated_at = now() WHERE id = '{publicationId:D}'",
                    cancellationToken).ConfigureAwait(false);
            }

            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
