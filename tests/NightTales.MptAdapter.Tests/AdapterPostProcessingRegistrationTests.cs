using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;
using Shouldly;
using Xunit;

namespace NightTales.MptAdapter.Tests;

/// <summary>QA-3: the extension a host calls builds a real provider whose poller and post-processor share ONE feed.</summary>
public sealed class AdapterPostProcessingRegistrationTests
{
    [Fact]
    public void AddAdapterPostProcessing_ResolvesTheWholeGraph_WithOneSharedFeed()
    {
        var root = Path.Combine(Path.GetTempPath(), "nts-di-" + Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Adapter:DataDir"] = Path.Combine(root, "adapter"),
                ["Adapter:MptRoot"] = Path.Combine(root, "mpt"),
                ["Adapter:RelayDir"] = Path.Combine(root, "relay"),
                ["Adapter:MptBaseUrl"] = "http://127.0.0.1:8080/",
                ["Adapter:CheckTimeoutSeconds"] = "123",
                ["Adapter:MaxVideoBytes"] = "4096",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITaskStore>(new InMemoryTaskStore());
        services.AddSingleton(Options.Create(new RenderNodeServerOptions { MaxConcurrentTasks = 1 }));
        services.AddAdapterPostProcessing(configuration);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var hosted = provider.GetServices<IHostedService>().OfType<PostProcessor>().ShouldHaveSingleItem();
        hosted.ShouldBeSameAs(provider.GetRequiredService<PostProcessor>());
        provider.GetRequiredService<MptClient>().ShouldBeSameAs(provider.GetRequiredService<IRenderBackend>());

        var feed = provider.GetRequiredService<ICompletedTaskFeed>();
        feed.ShouldBeSameAs(provider.GetRequiredService<ChannelCompletedTaskFeed>());
        FeedOf(hosted, "_feed").ShouldBeSameAs(feed);
        FeedOf(provider.GetRequiredService<MptTaskPoller>(), "_completedTasks").ShouldBeSameAs(feed);

        var bound = provider.GetRequiredService<IOptions<AdapterOptions>>().Value;
        bound.CheckTimeoutSeconds.ShouldBe(123);
        bound.MaxVideoBytes.ShouldBe(4096);
    }

    private static object FeedOf(object owner, string field) =>
        owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
}
