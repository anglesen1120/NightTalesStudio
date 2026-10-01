using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightTales.Domain.Catalog;
using NightTales.Domain.Common;
using NightTales.Infrastructure.Storage;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Catalog;

/// <summary>
/// The log acceptance check of 09.4.2-T1 (plan §8, task §Acceptance): a catalog request logs neither the
/// stored key nor the composed URL, and the empty-base-URL warning is a start-up line, not a per-request
/// one. The sink is a hand-written <see cref="ILoggerProvider"/> — no mocking library (09 README D13).
/// </summary>
/// <remarks>
/// The scan is over every line the host renders, so a value that leaked anywhere — the request log, the
/// mediator line, an EF command — would be caught by the negative assertions. Each test also asserts a
/// line it *expects*, so a sink that captured nothing cannot pass vacuously.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CatalogLoggingTests(TestDatabase database) : IntegrationTest(database)
{
    private const string VoicesRoute = "/api/v1/voices";
    private const string StyleTemplatesRoute = "/api/v1/style-templates";

    /// <summary>The public bucket base URL of the leak test.</summary>
    private const string PublicBaseUrl = "https://nt-public-dev.example.test";

    /// <summary>The warning of <c>PublicAssetUrlWarning</c>; the value is the event name, never a key.</summary>
    private const string MissingBaseUrlWarning = "storage.public-asset-url.missing-base-url";

    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private static string PublicBaseUrlKey =>
        $"{StorageOptions.SectionName}:{nameof(StorageOptions.PublicBaseUrl)}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EmptyBaseUrl_WarnsOnceAtStartup_NotPerRequest()
    {
        var sink = new CapturingLoggerProvider();

        // The plain host: `Storage:PublicBaseUrl` is empty (its committed default).
        using var host = new ApiFactory(Database, new FakeClock(Start))
            .WithWebHostBuilder(builder => builder
                .ConfigureServices(services => services.AddLogging(logging => logging.AddProvider(sink))));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());

        using var first = await client.GetAsync(VoicesRoute, Ct);
        using var second = await client.GetAsync(StyleTemplatesRoute, Ct);

        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(Ct));
        second.StatusCode.ShouldBe(HttpStatusCode.OK, await second.Content.ReadAsStringAsync(Ct));

        WarningCount(sink).ShouldBe(1, "the empty base URL is a start-up condition, not a per-request one");
    }

    [Fact]
    public async Task Requests_Log_Neither_The_Key_Nor_The_Public_Url()
    {
        const string Key = "voice-previews/nts-log-t1.v1.mp3";
        await SeedVoiceAsync($"nts-v-{Guid.CreateVersion7():N}", Key);

        var sink = new CapturingLoggerProvider();
        using var host = new ApiFactory(Database, new FakeClock(Start))
            .WithWebHostBuilder(builder => builder
                .UseSetting(PublicBaseUrlKey, PublicBaseUrl)
                .ConfigureServices(services => services.AddLogging(logging => logging.AddProvider(sink))));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());

        using var voices = await client.GetAsync(VoicesRoute, Ct);
        using var templates = await client.GetAsync(StyleTemplatesRoute, Ct);

        voices.StatusCode.ShouldBe(HttpStatusCode.OK, await voices.Content.ReadAsStringAsync(Ct));
        templates.StatusCode.ShouldBe(HttpStatusCode.OK, await templates.Content.ReadAsStringAsync(Ct));
        (await voices.Content.ReadAsStringAsync(Ct)).ShouldContain(Key, Case.Sensitive, "the response does carry the composed preview URL");

        var logs = sink.Text;

        // Non-vacuous: the catalog requests really were logged through this sink.
        logs.ShouldContain(
            "ListVoicesQuery",
            Case.Sensitive,
            "the mediator log line of the catalog query is what this scan reads");
        logs.ShouldNotContain(Key, Case.Sensitive, "a stored key must never be logged (plan §8)");
        logs.ShouldNotContain(PublicBaseUrl, Case.Sensitive, "a composed preview URL must never be logged (plan §8)");
        WarningCount(sink).ShouldBe(0, "a configured base URL warns about nothing");
    }

    private static int WarningCount(CapturingLoggerProvider sink) =>
        sink.Lines.Count(line => line.Contains(MissingBaseUrlWarning, StringComparison.Ordinal));

    private async Task SeedVoiceAsync(string id, string previewStorageKey)
    {
        await using var context = Database.CreateAppDbContext(new TestCurrentUser());
        context.Voices.Add(Voice.Create(id, id, Language.Vi, VoiceGender.Female, previewStorageKey: previewStorageKey));
        await context.SaveChangesAsync(Ct);
    }

    /// <summary>Keeps every rendered line; asserts that no line carries a key or a composed URL.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];
        private readonly Lock _gate = new();

        internal IReadOnlyList<string> Lines
        {
            get
            {
                lock (_gate)
                {
                    return [.. _messages];
                }
            }
        }

        internal string Text => string.Join('\n', Lines);

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private void Capture(string rendered)
        {
            lock (_gate)
            {
                _messages.Add(rendered);
            }
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                owner.Capture(formatter(state, exception));
            }
        }
    }
}
