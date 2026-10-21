using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NightTales.TestKit.Fetching;

/// <summary>One request the <see cref="HttpTestServer"/> received.</summary>
/// <param name="Method">The method.</param>
/// <param name="Target">The request target (path and query).</param>
/// <param name="Host">The <c>Host</c> header value.</param>
/// <param name="Headers">The headers, case-insensitive.</param>
public sealed record TestRequest(string Method, string Target, string Host, IReadOnlyDictionary<string, string> Headers);

/// <summary>One scripted answer of the <see cref="HttpTestServer"/>.</summary>
/// <param name="Status">The status code.</param>
/// <param name="ContentType">The <c>Content-Type</c> value, or <see langword="null"/> for none.</param>
/// <param name="Body">The body bytes (sent as-is: pass gzip bytes with a <c>Content-Encoding</c> header).</param>
public sealed record TestResponse(int Status, string? ContentType = null, byte[]? Body = null)
{
    /// <summary>Extra headers, for example <c>Location</c> or <c>Content-Encoding</c>.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>A pause before answering; the request's cancellation ends it.</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.Zero;

    /// <summary>A UTF-8 text answer.</summary>
    public static TestResponse Text(int status, string contentType, string body) =>
        new(status, contentType, Encoding.UTF8.GetBytes(body));

    /// <summary>A <c>302</c> to <paramref name="location"/>.</summary>
    public static TestResponse Redirect(string location, int status = 302) =>
        new(status) { Headers = new Dictionary<string, string> { ["Location"] = location } };
}

/// <summary>
/// A minimal loopback HTTP/1.1 server for the fetcher tests (02 D20). It listens on <c>127.0.0.1</c> at a random port,
/// answers each request from a script with <c>Connection: close</c>, and records every accepted connection and every
/// request, so a test can assert that a fetch did <em>not</em> reach it (the DNS-rebinding control). The server is only
/// reachable by a fetcher built with the <see cref="LoopbackAllowingAddressValidator"/>.
/// </summary>
public sealed class HttpTestServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<(TestRequest Request, DateTimeOffset Time)> _received = new();
    private readonly Func<TestRequest, TestResponse> _script;
    private readonly Task _accepting;
    private readonly ConcurrentBag<Task> _clients = [];
    private int _connections;

    /// <summary>Starts the server with <paramref name="script"/> answering every request.</summary>
    /// <param name="script">Maps a request to its answer.</param>
    public HttpTestServer(Func<TestRequest, TestResponse> script)
    {
        ArgumentNullException.ThrowIfNull(script);

        _script = script;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = AcceptAsync();
    }

    /// <summary>The loopback port the server listens on.</summary>
    public int Port { get; }

    /// <summary>The TCP connections accepted so far, whether or not they sent a request.</summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>The requests received, in arrival order.</summary>
    public IReadOnlyList<TestRequest> Requests => [.. _received.ToArray().Select(r => r.Request)];

    /// <summary>When each request of <see cref="Requests"/> arrived, in the same order (rate-limit assertions).</summary>
    public IReadOnlyList<DateTimeOffset> RequestTimes => [.. _received.ToArray().Select(r => r.Time)];

    /// <summary>
    /// A port nothing listens on, so a connect to it is refused at once. A fetch test maps every <em>non-loopback</em>
    /// validated address here, so "connected to the public address" never leaves the machine.
    /// </summary>
    public static int ClosedPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();

        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    /// <summary>
    /// The <c>endpointFor</c> of <c>SafeHttpConnector</c> for tests: a validated loopback address is dialled at the server
    /// port (a source URL may only use 80/443, so the real port is substituted after the address check); any other validated
    /// address is dialled at <paramref name="closedPort"/> and is refused.
    /// </summary>
    /// <param name="closedPort">A port that refuses connections, see <see cref="ClosedPort"/>.</param>
    public Func<IPAddress, int, IPEndPoint> EndpointFor(int closedPort) =>
        (address, _) => IPAddress.IsLoopback(address)
            ? new IPEndPoint(address, Port)
            : new IPEndPoint(IPAddress.Loopback, closedPort);

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _connections);
                _clients.Add(ServeAsync(client));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Stopping.
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                _received.Enqueue((request, DateTimeOffset.UtcNow));
                var response = _script(request);
                if (response.Delay > TimeSpan.Zero)
                {
                    await Task.Delay(response.Delay, _stop.Token).ConfigureAwait(false);
                }

                var body = response.Body ?? [];
                var head = new StringBuilder()
                    .Append("HTTP/1.1 ").Append(response.Status).Append(" X\r\n")
                    .Append("Content-Length: ").Append(body.Length).Append("\r\n")
                    .Append("Connection: close\r\n");
                if (response.ContentType is not null)
                {
                    head.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
                }

                foreach (var (name, value) in response.Headers)
                {
                    head.Append(name).Append(": ").Append(value).Append("\r\n");
                }

                head.Append("\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), _stop.Token).ConfigureAwait(false);
                await stream.WriteAsync(body, _stop.Token).ConfigureAwait(false);
                await stream.FlushAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // The client went away (a timeout, or a connection the test never meant to complete).
            }
        }
    }

    private async Task<TestRequest?> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (buffer.Count < 16 * 1024)
        {
            var read = await stream.ReadAsync(one, _stop.Token).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            buffer.Add(one[0]);
            var count = buffer.Count;
            if (count >= 4 && buffer[count - 4] == '\r' && buffer[count - 3] == '\n' && buffer[count - 2] == '\r' && buffer[count - 1] == '\n')
            {
                break;
            }
        }

        var lines = Encoding.ASCII.GetString([.. buffer]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var first = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        return new TestRequest(first[0], first[1], headers.GetValueOrDefault("Host", string.Empty), headers);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        await _accepting.ConfigureAwait(false);
        await Task.WhenAll(_clients).ConfigureAwait(false);
        _stop.Dispose();
    }
}
