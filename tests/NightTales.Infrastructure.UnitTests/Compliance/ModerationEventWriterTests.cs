using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Common;
using NightTales.Domain.Compliance;
using NightTales.Infrastructure.Compliance;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Security;
using NightTales.Infrastructure.UnitTests.OpenAI;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Compliance;

/// <summary>
/// 02.4.1-T1 (02 README D12, D19): the <c>moderation_events</c> writer. The excerpt is the only content-bearing
/// column and is cut from our own brief or draft at 300 characters; the stage is the wire name; a database
/// failure is swallowed because the row is evidence, not control flow. Rows are read back by user id, so the
/// shared class database cannot couple the tests.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ModerationEventWriterTests(TestDatabase database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ModerationEvent_ExcerptNeverExceeds300Chars()
    {
        await using var host = new WriterHost(Database);
        var user = await Database.CreateUserBuilder().CreateAsync(ct: Ct);
        var ownText = new string('x', 5_000);

        await host.Writer.WriteAsync(user.Id, null, ModerationSource.OpenaiText, ModerationStage.Draft, ["violence"], ownText, Ct);

        var row = (await Rows(user.Id)).ShouldHaveSingleItem();
        row.ExcerptLength.ShouldBe(ModerationEvent.ExcerptMaxLength);
        row.ExcerptLength.ShouldBeLessThanOrEqualTo(300);
    }

    [Fact]
    public async Task ModerationEvent_ExcerptComesFromOurText_NotFromSources()
    {
        await using var host = new WriterHost(Database);
        var user = await Database.CreateUserBuilder().CreateAsync(ct: Ct);

        // The writer takes our own brief or draft only (D19, D22): a series-level or text-less check has no excerpt.
        await host.Writer.WriteAsync(user.Id, null, ModerationSource.OpenaiText, ModerationStage.Brief, ["hate"], "our own brief", Ct);
        await host.Writer.WriteAsync(user.Id, null, ModerationSource.OpenaiText, ModerationStage.FindSources, ["hate"], ownText: null, Ct);

        var rows = await Rows(user.Id);
        rows.Single(r => r.Stage == "brief").Excerpt.ShouldBe("our own brief");
        rows.Single(r => r.Stage == "find_sources").Excerpt.ShouldBeEmpty();
    }

    [Fact]
    public async Task Moderator_SeriesLevelStage_RecordsStageKind()
    {
        await using var host = new WriterHost(Database);
        var user = await Database.CreateUserBuilder().CreateAsync(ct: Ct);

        await host.Writer.WriteAsync(user.Id, null, ModerationSource.OpenaiText, ModerationStage.BibleProposal, ["violence"], "bible", Ct);

        var row = (await Rows(user.Id)).ShouldHaveSingleItem();
        row.Stage.ShouldBe("bible_proposal");
        row.Source.ShouldBe("openai_text");
        row.EpisodeId.ShouldBeEmpty("a series-level run touches no episode");
        row.Categories.ShouldBe("violence");
    }

    [Fact]
    public async Task ModerationEvent_DraftStage_WritesEpisodeSourceAndCategories()
    {
        await using var host = new WriterHost(Database);
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor).CreateAsync(user.Id, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor).CreateAsync(user.Id, series.Id, ct: Ct);

        await host.Writer.WriteAsync(
            user.Id,
            episode.Id,
            ModerationSource.OpenaiText,
            ModerationStage.Draft,
            ["hate/threatening", "violence"],
            "draft text",
            Ct);

        var row = (await Rows(user.Id)).ShouldHaveSingleItem();
        row.Stage.ShouldBe("draft");
        row.Source.ShouldBe("openai_text");
        row.EpisodeId.ShouldBe(episode.Id.ToString());
        row.Categories.ShouldBe("hate/threatening,violence");
    }

    [Fact]
    public async Task ModerationEventWriter_DatabaseFailure_IsSwallowed()
    {
        await using var host = new WriterHost(Database);

        // No such user: the FK rejects the row. The run outcome must not change, so nothing escapes.
        await Should.NotThrowAsync(async () =>
            await host.Writer.WriteAsync(Guid.NewGuid(), null, ModerationSource.OpenaiText, ModerationStage.Brief, ["hate"], "text", Ct));
    }

    // ---- QA additions (02.4.1-T1) ----

    [Fact]
    public async Task ModerationEvent_ExcerptCutAcrossASurrogatePair_StillWritesTheRow()
    {
        await using var host = new WriterHost(Database);
        var user = await Database.CreateUserBuilder().CreateAsync(ct: Ct);
        var ownText = new string('a', 299) + "\U0001F600" + "tail";

        await host.Writer.WriteAsync(user.Id, null, ModerationSource.OpenaiText, ModerationStage.Draft, ["violence"], ownText, Ct);

        var row = (await Rows(user.Id)).ShouldHaveSingleItem("the evidence row must survive an emoji at the 300-char cut");
        row.ExcerptLength.ShouldBeLessThanOrEqualTo(300);
    }

    [Fact]
    public async Task ModerationEvent_ExcerptOfExactly300And301Chars_IsKeptAndCutAtTheCap()
    {
        await using var host = new WriterHost(Database);
        var user = await Database.CreateUserBuilder().CreateAsync(ct: Ct);

        await host.Writer.WriteAsync(user.Id, null, ModerationSource.OpenaiText, ModerationStage.Brief, ["hate"], new string('b', 300), Ct);
        await host.Writer.WriteAsync(user.Id, null, ModerationSource.OpenaiText, ModerationStage.Draft, ["hate"], new string('c', 301), Ct);

        var rows = await Rows(user.Id);
        rows.Single(r => r.Stage == "brief").ExcerptLength.ShouldBe(300);
        rows.Single(r => r.Stage == "draft").ExcerptLength.ShouldBe(300);
    }

    [Fact]
    public async Task ModerationEvent_EmptyAndWhitespaceText_WritesARowWithoutAnExcerptOrNeverThrows()
    {
        await using var host = new WriterHost(Database);
        var user = await Database.CreateUserBuilder().CreateAsync(ct: Ct);

        await Should.NotThrowAsync(async () =>
        {
            await host.Writer.WriteAsync(user.Id, null, ModerationSource.OpenaiText, ModerationStage.Brief, ["hate"], string.Empty, Ct);
            await host.Writer.WriteAsync(user.Id, null, ModerationSource.OpenaiText, ModerationStage.Draft, ["hate"], "   ", Ct);
        });

        (await Rows(user.Id)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task ModerationEventWriter_CancelledCallerToken_StillKeepsTheEvidence()
    {
        await using var host = new WriterHost(Database);
        var user = await Database.CreateUserBuilder().CreateAsync(ct: Ct);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.NotThrowAsync(async () =>
            await host.Writer.WriteAsync(user.Id, null, ModerationSource.OpenaiText, ModerationStage.Draft, ["violence"], "text", cts.Token));

        (await Rows(user.Id)).ShouldHaveSingleItem("a cancelled job still keeps its evidence (WriteTimeout is independent of the caller token)");
    }

    [Fact]
    public async Task ModerationEventWriter_CancelledCallerAndTimedOutSave_LogsTheDeadlineAndDoesNotThrow()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = new WriterHost(Database, logs, writeTimeout: TimeSpan.Zero);
        var user = await Database.CreateUserBuilder().CreateAsync(ct: Ct);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // The writer-owned deadline has already expired when the save starts, and the caller is cancelled too:
        // the expiry is still a write failure and must be logged, not swallowed as the job's cancellation.
        await Should.NotThrowAsync(async () =>
            await host.Writer.WriteAsync(user.Id, null, ModerationSource.OpenaiText, ModerationStage.Draft, ["violence"], "text", cts.Token));

        logs.Messages.ShouldContain(m => m.Contains("not written", StringComparison.Ordinal) && m.Contains("CanceledException", StringComparison.Ordinal));
        (await Rows(user.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ModerationEventWriter_CancelledCallerAndHealthySave_LogsNothing()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = new WriterHost(Database, logs);
        var user = await Database.CreateUserBuilder().CreateAsync(ct: Ct);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await host.Writer.WriteAsync(user.Id, null, ModerationSource.OpenaiText, ModerationStage.Draft, ["violence"], "text", cts.Token);

        logs.Messages.ShouldNotContain(m => m.Contains("not written", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ModerationEvent_LoneSurrogateInTheMiddleOfTheExcerpt_StillWritesTheRow()
    {
        await using var host = new WriterHost(Database);
        var user = await Database.CreateUserBuilder().CreateAsync(ct: Ct);

        await host.Writer.WriteAsync(user.Id, null, ModerationSource.OpenaiText, ModerationStage.Draft, ["violence"], "ab\uD83Dcd\uDE00ef", Ct);

        var row = (await Rows(user.Id)).ShouldHaveSingleItem("a lone surrogate must not lose the evidence row");
        row.Excerpt.ShouldBe("ab\uFFFDcd\uFFFDef");
    }

    [Fact]
    public async Task ModerationEventWriter_DatabaseFailure_LogsTheExceptionTypeOnly_NeverTheText()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = new WriterHost(Database, logs);

        await host.Writer.WriteAsync(Guid.NewGuid(), null, ModerationSource.OpenaiText, ModerationStage.Draft, ["hate"], "EXCERPT-TEXT-SECRET", Ct);

        logs.Messages.ShouldNotBeEmpty("the failure is logged");
        logs.Messages.ShouldNotContain(m => m.Contains("EXCERPT-TEXT-SECRET", StringComparison.Ordinal), "D19: the excerpt never reaches a log");
    }

    [Fact]
    public async Task ModerationEventWriter_SuccessfulWrite_LogsNothingWithTheText()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = new WriterHost(Database, logs);
        var user = await Database.CreateUserBuilder().CreateAsync(ct: Ct);

        await host.Writer.WriteAsync(user.Id, null, ModerationSource.OpenaiText, ModerationStage.Draft, ["hate"], "EXCERPT-TEXT-SECRET", Ct);

        logs.Messages.ShouldNotContain(m => m.Contains("EXCERPT-TEXT-SECRET", StringComparison.Ordinal));
    }

    private sealed record Row(string Stage, string Source, string EpisodeId, string Categories, string Excerpt, int ExcerptLength);

    private async Task<List<Row>> Rows(Guid userId)
    {
        var lines = await Database.QueryStringsAsync(
            "SELECT concat_ws('|', stage, source, coalesce(episode_id::text, ''), array_to_string(categories, ','), coalesce(excerpt, ''), coalesce(length(excerpt), 0)) "
            + $"FROM moderation_events WHERE user_id = '{userId}'",
            Ct);

        return [.. lines.Select(line =>
        {
            var part = line.Split('|');
            return new Row(part[0], part[1], part[2], part[3], part[4], int.Parse(part[5], System.Globalization.CultureInfo.InvariantCulture));
        })];
    }

    private sealed class WriterHost : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        internal WriterHost(TestDatabase database, CapturingLoggerProvider? logs = null, TimeSpan? writeTimeout = null)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [$"ConnectionStrings:{PersistenceOptions.ConnectionStringName}"] = database.ConnectionString,
                })
                .Build();

            var clock = new FakeClock();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton<IClock>(clock);
            services.AddPersistence(configuration);
            services.AddScoped<ScopedCurrentUser>();
            services.AddScoped<ICurrentUser>(provider => provider.GetRequiredService<ScopedCurrentUser>());
            services.AddScoped<ISystemContext>(provider => provider.GetRequiredService<ScopedCurrentUser>());
            _provider = services.BuildServiceProvider();

            Writer = new ModerationEventWriter(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                clock,
                logs is null
                    ? NullLogger<ModerationEventWriter>.Instance
                    : LoggerFactory.Create(builder => builder.AddProvider(logs)).CreateLogger<ModerationEventWriter>(),
                writeTimeout);
        }

        internal ModerationEventWriter Writer { get; }

        public ValueTask DisposeAsync() => _provider.DisposeAsync();
    }
}
