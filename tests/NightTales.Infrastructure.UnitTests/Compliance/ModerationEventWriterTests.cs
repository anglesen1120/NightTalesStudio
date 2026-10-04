using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Domain.Common;
using NightTales.Domain.Compliance;
using NightTales.Infrastructure.Compliance;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Security;
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

        internal WriterHost(TestDatabase database)
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
                NullLogger<ModerationEventWriter>.Instance);
        }

        internal ModerationEventWriter Writer { get; }

        public ValueTask DisposeAsync() => _provider.DisposeAsync();
    }
}
