using System.Collections.Concurrent;
using System.Net.Http;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Rendering;
using NightTales.Domain.Rendering;

namespace NightTales.TestKit.Rendering;

/// <summary>
/// A hand-written <see cref="IRenderNodeConnectionFactory"/> (no mocking library, 09 README D13) that hands
/// the poller a client over a scripted <see cref="ScriptedHttpMessageHandler"/> instead of opening a socket:
/// the scripted node of 11.3.3-T1's health-poller tests, so a tick never needs a network or a certificate.
/// </summary>
/// <remarks>
/// One handler exists per node name, so a node's answers are scripted independently even though the poller
/// pulls several nodes concurrently. The clients the factory hands out are pooled exactly as the production
/// factory's are, so a caller disposing one does not disturb the script.
/// </remarks>
public sealed class FakeRenderNodeConnectionFactory : IRenderNodeConnectionFactory, IDisposable
{
    private readonly ConcurrentDictionary<string, ScriptedHttpMessageHandler> _handlers = new(StringComparer.Ordinal);
    private readonly List<HttpClient> _clients = [];
    private bool _disposed;

    /// <summary>
    /// The timeout of every client this factory builds; the default is the production per-pull timeout
    /// (<see cref="RenderNodesOptions.PullTimeout"/>). A test lowers it to drive the timeout leg without
    /// waiting five seconds.
    /// </summary>
    public TimeSpan Timeout { get; set; } = RenderNodesOptions.PullTimeout;

    /// <summary>Scripts the answers of one node, in call order.</summary>
    /// <param name="nodeName">The node whose answers these are.</param>
    /// <param name="steps">At least one answer; the last one repeats.</param>
    /// <returns>This factory, for chaining.</returns>
    public FakeRenderNodeConnectionFactory Script(string nodeName, params ScriptedHttpStep[] steps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);
        ArgumentNullException.ThrowIfNull(steps);

        if (steps.Length == 0)
        {
            throw new ArgumentException("At least one scripted step is required.", nameof(steps));
        }

        if (_handlers.TryGetValue(nodeName, out var existing))
        {
            // A client already handed out holds this handler, so re-scripting updates it in place: swapping
            // the dictionary entry would leave a pooled client (the poller keeps one per node) on the old
            // answers, and the test would silently exercise the wrong phase.
            existing.Rescript(steps);
        }
        else
        {
            _handlers[nodeName] = new ScriptedHttpMessageHandler(steps);
        }

        return this;
    }

    /// <summary>The calls one node received, in order; empty when the node was never called.</summary>
    /// <param name="nodeName">The node's name.</param>
    public IReadOnlyList<RecordedNodeRequest> Requests(string nodeName) =>
        _handlers.TryGetValue(nodeName, out var handler) ? handler.Requests : [];

    /// <inheritdoc />
    public HttpClient Create(WorkerNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var handler = _handlers.GetOrAdd(
            node.Name,
            _ => new ScriptedHttpMessageHandler([new ScriptedHttpStep()]));

        var client = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = node.BaseUrl,
            Timeout = Timeout,
        };

        lock (_clients)
        {
            _clients.Add(client);
        }

        return client;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_clients)
        {
            foreach (var client in _clients)
            {
                client.Dispose();
            }

            _clients.Clear();
        }

        foreach (var handler in _handlers.Values)
        {
            handler.Dispose();
        }

        _handlers.Clear();
    }
}
