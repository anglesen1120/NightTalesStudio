using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NightTales.MockMpt.Backend;
using NightTales.MockMpt.Fixtures;
using NightTales.MockMpt.Health;
using NightTales.MockMpt.Images;
using NightTales.MockMpt.Media;
using NightTales.RenderNode.Server;

namespace NightTales.MockMpt;

/// <summary>
/// Wires the mock host (11.1.2-T1 Design): the core's options and middleware, the four node seams bound to the
/// mock's implementations, the fixtures, the canned media and the fake backend's ticker. The native run and the
/// tests build the same application, so a test exercises exactly what <c>dotnet run</c> starts.
/// </summary>
public static class MockHost
{
    /// <summary>
    /// Builds the application for <paramref name="options"/>. <paramref name="urls"/> overrides Kestrel's
    /// binding (<c>ASPNETCORE_URLS</c> natively, <c>http://127.0.0.1:0</c> in the tests); null leaves the
    /// environment/default in charge. <paramref name="minimumLevel"/> lowers the log level for a quiet host
    /// (the tests); null keeps the environment's configuration.
    /// </summary>
    /// <exception cref="MockStartupException">A fixture or media prerequisite is missing (exit code 2).</exception>
    public static WebApplication Create(MockMptOptions options, string? urls = null, LogLevel? minimumLevel = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var fixtures = FixtureSet.Load(options.FixturesDirectory);
        ValidateFixtureSelection(options, fixtures);
        var media = CannedMediaSet.Ensure(options.MediaDirectory);

        var builder = WebApplication.CreateBuilder();
        if (minimumLevel is { } level)
        {
            builder.Logging.SetMinimumLevel(level);
        }

        // The two core-owned values are bound from the mock's environment names (D14). Added after the default
        // providers, so the MOCK_* spelling wins over a bare RenderNodeServer__* value.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [RenderNodeServerOptions.SectionName + ":" + nameof(RenderNodeServerOptions.ApiKey)] = options.ApiKey,
            [RenderNodeServerOptions.SectionName + ":" + nameof(RenderNodeServerOptions.MaxConcurrentTasks)] =
                options.MaxConcurrentTasks.ToString(CultureInfo.InvariantCulture),
        });

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(fixtures);
        builder.Services.AddSingleton(media);
        builder.Services.AddSingleton<MockHealthState>();
        builder.Services.AddRenderNodeServer(builder.Configuration);
        builder.Services.AddSingleton<IImagePlanSink, MockImageReport>();
        builder.Services.AddSingleton<INodeHealthSource, MockNodeHealthSource>();

        // One instance is the backend and the ticker: the seam the core calls and the hosted service that
        // advances the clock are the same state machine.
        builder.Services.AddSingleton<FakeRenderBackend>();
        builder.Services.AddSingleton<IRenderBackend>(provider => provider.GetRequiredService<FakeRenderBackend>());
        builder.Services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<FakeRenderBackend>());

        if (!string.IsNullOrEmpty(urls))
        {
            builder.WebHost.UseUrls(urls);
        }

        var app = builder.Build();
        app.UseRenderNodeServer();
        app.MapRenderNodeV1();
        return app;
    }

    private static void ValidateFixtureSelection(MockMptOptions options, FixtureSet fixtures)
    {
        if (options.Fixture is { } fixture && !fixtures.CanReplay(fixture))
        {
            var accepted = string.Join(", ", fixtures.StatusIds.Concat(fixtures.HealthIds));
            throw new MockStartupException(
                $"MOCK_FIXTURE='{fixture}' is not a replayable fixture in '{fixtures.Directory}'; accepted: {accepted}");
        }
    }
}
