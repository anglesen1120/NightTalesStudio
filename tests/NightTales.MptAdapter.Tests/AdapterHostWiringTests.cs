using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using Shouldly;
using Xunit;

namespace NightTales.MptAdapter.Tests;

/// <summary>11.3.5 T3+T4 integration: the real adapter host graph wires the post-processor into the T4 host.</summary>
public sealed class AdapterHostWiringTests
{
    [Fact]
    public void AddMptAdapter_HostsPostProcessorAndPoller_OverOneFeedAndOneMptClient()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "nts-wiring-" + Guid.NewGuid().ToString("N"));
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Production });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Adapter:ListenAddress"] = "10.20.30.40",
            ["Adapter:DataDir"] = dataDir,
            ["RenderNodeServer:ApiKey"] = "adapter-key-0123456789abcdef0123456789",
        });
        builder.Services.AddMptAdapter(builder.Configuration);

        using var host = builder.Build();
        var provider = host.Services;

        var postProcessor = provider.GetServices<IHostedService>().OfType<PostProcessor>().ShouldHaveSingleItem();
        postProcessor.ShouldBeSameAs(provider.GetRequiredService<PostProcessor>());

        var poller = provider.GetServices<IHostedService>().OfType<MptTaskPoller>().ShouldHaveSingleItem();
        poller.ShouldBeSameAs(provider.GetRequiredService<MptTaskPoller>());

        var feed = provider.GetRequiredService<ICompletedTaskFeed>();
        feed.ShouldBeSameAs(provider.GetRequiredService<ChannelCompletedTaskFeed>());
        FieldOf(postProcessor, "_feed").ShouldBeSameAs(feed);
        FieldOf(poller, "_completedTasks").ShouldBeSameAs(feed);

        provider.GetRequiredService<IRenderBackend>().ShouldBeSameAs(provider.GetRequiredService<MptClient>());
        provider.GetRequiredService<ITaskStore>().ShouldBeOfType<SqliteTaskStore>();
    }

    private static object FieldOf(object owner, string field) =>
        owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
}
