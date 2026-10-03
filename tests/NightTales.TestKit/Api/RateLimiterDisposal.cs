using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace NightTales.TestKit.Api;

/// <summary>
/// Disposes the rate-limiting middleware's endpoint limiter when a test host's service provider is
/// disposed, which ASP.NET Core never does itself.
/// </summary>
/// <remarks>
/// <para>
/// <c>RateLimitingMiddleware</c> creates a <c>PartitionedRateLimiter</c> in its constructor and keeps it
/// in a private field; nothing disposes it with the host. The limiter runs a 100 ms replenish timer
/// (<c>TimerAwaitable</c>) that the runtime's static timer queue keeps alive, and the limiter's partitioner
/// closure references the middleware, so the middleware's <c>_next</c> chain and with it the whole host —
/// the service provider, its option caches, the endpoint data sources — stays reachable after the
/// factory is disposed. A heap dump of the CI integration run showed 330 disposed <c>ApiFactory</c>
/// hosts kept alive this way (about 15 MB each) until the 2-core runner ran out of memory.
/// </para>
/// <para>
/// The filter adds the first component of the host's pipeline. That component is built last, so it is
/// handed the finished pipeline; it walks the pipeline's delegates to the rate-limiting middleware, reads
/// the limiter and hands it to a singleton that disposes it with the provider. The middleware type is
/// internal to the framework, hence the reflection; if a framework upgrade renames it, building the host
/// fails with a message that names this type instead of the leak coming back silently.
/// </para>
/// </remarks>
internal sealed class RateLimiterDisposal : IStartupFilter
{
    private const string MiddlewareTypeName = "Microsoft.AspNetCore.RateLimiting.RateLimitingMiddleware";
    private const string LimiterFieldName = "_endpointLimiter";

    private const BindingFlags InstanceFields =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    /// <summary>Adds the filter and the singleton that owns the limiter to a host's service collection.</summary>
    internal static void Register(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<LimiterOwner>();
        services.AddSingleton<IStartupFilter, RateLimiterDisposal>();
    }

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return application =>
        {
            application.Use(pipeline =>
            {
                application.ApplicationServices.GetRequiredService<LimiterOwner>().Adopt(FindLimiter(pipeline));

                return pipeline;
            });

            next(application);
        };
    }

    /// <summary>
    /// Walks the delegate chain from <paramref name="pipeline"/> through middleware instances and compiler
    /// closures (never through services or collections) until it reaches the rate-limiting middleware.
    /// </summary>
    private static IDisposable FindLimiter(RequestDelegate pipeline)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Queue<object>();
        Enqueue(pipeline);

        while (pending.TryDequeue(out var node))
        {
            var type = node.GetType();
            if (type.FullName == MiddlewareTypeName)
            {
                return type.GetField(LimiterFieldName, InstanceFields)?.GetValue(node) as IDisposable
                    ?? throw Unsupported($"{MiddlewareTypeName} has no disposable '{LimiterFieldName}' field");
            }

            for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
            {
                foreach (var field in current.GetFields(InstanceFields))
                {
                    if (!field.FieldType.IsValueType)
                    {
                        Enqueue(field.GetValue(node));
                    }
                }
            }
        }

        throw Unsupported($"{MiddlewareTypeName} is not part of the host's pipeline");

        void Enqueue(object? value)
        {
            var target = value is Delegate delegateValue ? delegateValue.Target : value;
            if (target is not null && IsPipelineObject(target.GetType()) && seen.Add(target))
            {
                pending.Enqueue(target);
            }
        }
    }

    /// <summary>
    /// Middleware, their options and the closures that bind them; service providers, loggers and
    /// collections are outside these namespaces, which keeps the walk to the delegate chain.
    /// </summary>
    private static bool IsPipelineObject(Type type) =>
        type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
        || type.Namespace is { } ns
            && (ns.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal)
                || ns.StartsWith("Serilog.AspNetCore", StringComparison.Ordinal)
                || ns.StartsWith("NightTales.", StringComparison.Ordinal));

    private static InvalidOperationException Unsupported(string reason) =>
        new($"{nameof(RateLimiterDisposal)} cannot find the rate limiter to dispose ({reason}). "
            + "Test hosts would keep their whole service graph alive after disposal; update the walk for the new framework shape.");

    /// <summary>Holds the limiter for the host's lifetime and disposes it with the root provider.</summary>
    private sealed class LimiterOwner : IDisposable
    {
        private IDisposable? _limiter;

        internal void Adopt(IDisposable limiter) => _limiter = limiter;

        public void Dispose() => _limiter?.Dispose();
    }
}
