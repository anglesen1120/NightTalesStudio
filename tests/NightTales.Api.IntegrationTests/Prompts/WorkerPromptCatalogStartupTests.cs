using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Infrastructure.Prompts;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Threading;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Prompts;

/// <summary>
/// The Worker's half of 02.2.3-T1: the real Worker composition root refuses to start when the catalog
/// is invalid, and the error names every violation — which in a deployed Worker is the non-zero exit of
/// <c>dotnet run --project src/NightTales.Worker</c>.
/// </summary>
/// <remarks>
/// In the "Hangfire server" collection: the Worker graph installs Hangfire's schema and its
/// process-global log provider, which the hosts of that collection share.
/// </remarks>
[Collection("Hangfire server")]
public sealed class WorkerPromptCatalogStartupTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task WorkerStartup_BadCatalog_ExitsNonZeroWithViolations()
    {
        await using var host = new WorkerHost(Database, new FakeClock());
        host.ConfigureServices(services =>
        {
            // The catalog is embedded, so a deleted file cannot be simulated from a test; one resource
            // name is dropped instead, which is exactly what the gate sees after `rm
            // prompts/story/ghost.vi.v1.md` and a rebuild.
            services.RemoveAll<IPromptResourceSource>();
            services.AddSingleton<IPromptResourceSource>(new MissingGhostViPresetSource());
        });

        var exception = await Should.ThrowAsync<Exception>(() => host.StartAsync(TestContextToken.Value));

        var failure = FindCatalogFailure(exception)
            ?? throw new InvalidOperationException(
                $"The Worker did not fail with a {nameof(PromptCatalogException)}: {exception}");

        failure.Message.ShouldContain("missing preset ghost.vi.v1");
        failure.Message.ShouldContain("manifest file 'story/ghost.vi.v1.md' is not an embedded resource (not found)");
        failure.Violations.Count.ShouldBeGreaterThanOrEqualTo(2);
    }

    /// <summary>
    /// The failure behind a start-up error, whatever the host wrapped it in: a failure of a hosted
    /// service or of a singleton's construction can surface as itself or inside an
    /// <see cref="AggregateException"/>, depending on where the host resolves it.
    /// </summary>
    private static PromptCatalogException? FindCatalogFailure(Exception? exception)
    {
        if (exception is null)
        {
            return null;
        }

        if (exception is PromptCatalogException catalog)
        {
            return catalog;
        }

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                if (FindCatalogFailure(inner) is { } found)
                {
                    return found;
                }
            }
        }

        return FindCatalogFailure(exception.InnerException);
    }

    /// <summary>The real embedded tree with <c>story/ghost.vi.v1.md</c> missing, and nothing else changed.</summary>
    private sealed class MissingGhostViPresetSource : IPromptResourceSource
    {
        private const string GhostVi = ".story.ghost.vi.v1.md";

        private readonly EmbeddedPromptResourceSource _inner = new();

        internal MissingGhostViPresetSource() =>
            LogicalNames = [.. _inner.LogicalNames.Where(name => !name.EndsWith(GhostVi, StringComparison.Ordinal))];

        public IReadOnlyCollection<string> LogicalNames { get; }

        public byte[] Read(string logicalName) => _inner.Read(logicalName);
    }
}
