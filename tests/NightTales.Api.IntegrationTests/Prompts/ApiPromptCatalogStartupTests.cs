using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Api.IntegrationTests.Harness;
using NightTales.Application.Abstractions.Prompts;
using NightTales.Domain.Common;
using NightTales.Domain.Series;
using NightTales.Infrastructure.Prompts;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Prompts;

/// <summary>
/// The Api's half of 02.2.3-T1: the production composition root — <c>Program</c>, not the test-scheme
/// factory — loads the real embedded prompt catalog and serves traffic, so the start-up gate is part of
/// the host a deployment runs.
/// </summary>
public sealed class ApiPromptCatalogStartupTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task ApiStartup_GoodCatalog_ServesHealthOk()
    {
        using var host = ApiFactory.ProductionHost(Database.ConnectionString);
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/health/live", TestContextToken.Value);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var scope = host.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IPromptCatalog>();

        catalog.GetPreset(Topic.Ghost, Language.Vi).ShouldNotBeNullOrWhiteSpace();
        catalog.GetSystemPrompt().ShouldNotBeNullOrWhiteSpace();
        catalog.Files.ShouldContainKey("schemas/story.v1.json");
    }

    /// <summary>
    /// The Api half of the acceptance check the Worker test covers for its own host: a catalog that
    /// violates the manifest fails the start-up gate and names every violation, so a deployment exits
    /// non-zero rather than serving drafts built from an unreleased prompt set.
    /// </summary>
    [Fact]
    public void ApiStartup_BadCatalog_FailsWithViolations()
    {
        using var host = ApiFactory.ProductionHost(
            Database.ConnectionString,
            builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPromptResourceSource>();
                services.AddSingleton<IPromptResourceSource>(new MissingTrueCrimeEnPresetSource());
            }));

        var exception = Should.Throw<Exception>(() => host.CreateClient());

        var failure = FindCatalogFailure(exception)
            ?? throw new InvalidOperationException(
                $"The Api did not fail with a {nameof(PromptCatalogException)}: {exception}");

        failure.Message.ShouldContain("missing preset true_crime.en.v1");
        failure.Message.ShouldContain("manifest file 'story/true_crime.en.v1.md' is not an embedded resource (not found)");
        failure.Violations.Count.ShouldBeGreaterThanOrEqualTo(2);
    }

    /// <summary>
    /// The failure behind a start-up error, whatever the host wrapped it in: the same walk as the
    /// Worker test, kept local because the two tests live in different hosts' failure shapes.
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

    /// <summary>The real embedded tree with <c>story/true_crime.en.v1.md</c> missing, and nothing else changed.</summary>
    private sealed class MissingTrueCrimeEnPresetSource : IPromptResourceSource
    {
        private const string TrueCrimeEn = ".story.true_crime.en.v1.md";

        private readonly EmbeddedPromptResourceSource _inner = new();

        internal MissingTrueCrimeEnPresetSource() =>
            LogicalNames = [.. _inner.LogicalNames.Where(name => !name.EndsWith(TrueCrimeEn, StringComparison.Ordinal))];

        public IReadOnlyCollection<string> LogicalNames { get; }

        public byte[] Read(string logicalName) => _inner.Read(logicalName);
    }
}
