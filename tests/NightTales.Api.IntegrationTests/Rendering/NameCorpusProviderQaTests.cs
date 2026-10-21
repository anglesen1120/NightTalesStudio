using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Features.Rendering;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Keywords;
using NightTales.Infrastructure.Persistence;
using NightTales.Infrastructure.Persistence.Usage;
using NightTales.TestKit;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Builders;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Rendering;

/// <summary>
/// QA of <c>NameCorpusProvider</c> and of the <c>Images:Linter</c> binding (03.2.2-T1) through the production
/// <c>AddUsageMetering</c> graph: an unknown or invisible episode and a true-crime episode with no research both
/// answer with the <see cref="NameCorpus.Empty"/> singleton and never throw, and
/// <c>Images:Linter:NameCorpusEnabled</c> binds from configuration — present as either value, and absent as the
/// documented default <c>true</c>.
/// </summary>
/// <remarks>
/// The read path itself (tenant filter, fiction, draft sheet, non-url and non-fetched sources) is covered by
/// <c>NameCorpusProviderTests</c>; this class adds the missing-input and configuration rows. No verb or host is
/// started, so the provider is driven directly, as its production callers do.
/// </remarks>
/// <param name="database">The class database, injected by xUnit as the class fixture.</param>
public sealed class NameCorpusProviderQaTests(TestDatabase database) : IntegrationTest(database)
{
    private const string NameCorpusEnabledKey = "Images:Linter:NameCorpusEnabled";

    private static readonly DateTimeOffset Start = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Provider_UnknownEpisodeId_ReturnsEmptySingleton()
    {
        using var host = BuildHost();

        var random = await LoadAsync(host, Guid.CreateVersion7(), Guid.CreateVersion7());
        var empty = await LoadAsync(host, Guid.CreateVersion7(), Guid.Empty);

        random.ShouldBeSameAs(NameCorpus.Empty);
        empty.ShouldBeSameAs(NameCorpus.Empty);
        random.Matches("portrait of van an at the market").ShouldBeFalse();
    }

    [Fact]
    public async Task Provider_TrueCrimeEpisodeWithoutResearch_ReturnsEmptySingleton()
    {
        var actor = new TestCurrentUser();
        var user = await new UserBuilder(Database, actor).CreateAsync(ct: Ct);
        var series = await new SeriesBuilder(Database, actor, new FakeClock(Start))
            .CreateAsync(user.Id, topic: Topic.TrueCrime, ct: Ct);
        var episode = await new EpisodeBuilder(Database, actor, new FakeClock(Start))
            .CreateAsync(user.Id, series.Id, ct: Ct);

        using var host = BuildHost();

        var corpus = await LoadAsync(host, user.Id, episode.Id);

        corpus.ShouldBeSameAs(NameCorpus.Empty);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Options_LinterSwitch_BindsFromConfiguration(string? configured, bool expected)
    {
        using var host = BuildHost(configured);

        // Options resolution runs the production IValidateOptions, so a bad Images section would throw here.
        var options = host.Services.GetRequiredService<IOptions<ImagesOptions>>().Value;

        options.Linter.NameCorpusEnabled.ShouldBe(expected);
    }

    /// <summary>Builds the production <c>AddUsageMetering</c> graph over the class database.</summary>
    /// <param name="nameCorpusEnabled">The <c>Images:Linter:NameCorpusEnabled</c> value; null leaves the key absent.</param>
    /// <returns>The host; the caller disposes it.</returns>
    private RenderNodeTestHost BuildHost(string? nameCorpusEnabled = null)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"ConnectionStrings:{PersistenceOptions.ConnectionStringName}"] = Database.ConnectionString,
        };

        if (nameCorpusEnabled is not null)
        {
            settings[NameCorpusEnabledKey] = nameCorpusEnabled;
        }

        return RenderNodeTestHost.Build(
            Database,
            new FakeClock(Start),
            settings,
            configure: services => services.AddUsageMetering());
    }

    /// <summary>Resolves the provider in a fresh scope whose actor is <paramref name="callerId"/>.</summary>
    private static async Task<NameCorpus> LoadAsync(RenderNodeTestHost host, Guid callerId, Guid episodeId)
    {
        ((TestCurrentUser)host.Services.GetRequiredService<ICurrentUser>()).Id = callerId;

        using var scope = host.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<INameCorpusProvider>().LoadAsync(episodeId, Ct);
    }
}
