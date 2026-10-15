using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Features.Research.SourceFinder;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Research;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.Research;

/// <summary>Start-up behaviour of the allow-list validator with zero approved sites, and the committed file's coverage (02.7.2-T1).</summary>
public sealed class ResearchAllowListStartupTests
{
    private const string CandidateDomain = "candidate.example.org";

    [Fact]
    public async Task StartAsync_ZeroApprovedEntries_StartsAndLogsWarning()
    {
        var bytes = ResearchAllowListFixtures.Bytes(ResearchAllowListFixtures.Document(
            ResearchAllowListFixtures.Site(CandidateDomain, "candidate"),
            ResearchAllowListFixtures.Site("rejected.example.org", "rejected")));
        var snapshot = ResearchAllowListFixtures.Validator().Validate(bytes);
        var allowList = new EmbeddedResearchAllowList(snapshot, ResearchAllowListFixtures.NoBlocks);
        var logger = new CapturingLogger<ResearchAllowListStartupValidator>();

        var services = new ServiceCollection();
        services.AddSingleton<IResearchAllowList>(allowList);
        var hosted = new ResearchAllowListStartupValidator(services.BuildServiceProvider(), logger);

        await hosted.StartAsync(CancellationToken.None);

        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        warnings.Count.ShouldBe(1);

        // Log lines carry counts and the hash only: no domain names, no free text from the file.
        foreach (var entry in logger.Entries)
        {
            entry.Message.ShouldNotContain(CandidateDomain);
            entry.Message.ShouldNotContain("rejected.example.org");
        }

        logger.Entries.ShouldContain(e => e.Level == LogLevel.Information && e.Message.Contains(allowList.Hash, StringComparison.Ordinal) && e.Message.Contains("0 approved", StringComparison.Ordinal));
    }

    [Fact]
    public void CommittedAllowList_HasAtLeastThreeApprovedPerLanguage()
    {
        var list = EmbeddedResearchAllowList.LoadEmbedded(ResearchAllowListFixtures.Psl, ResearchAllowListFixtures.NoBlocks);

        list.Approved(Language.Vi).Count.ShouldBeGreaterThanOrEqualTo(3);
        list.Approved(Language.En).Count.ShouldBeGreaterThanOrEqualTo(3);
    }

    /// <summary>Records every log entry as its level and formatted message.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
