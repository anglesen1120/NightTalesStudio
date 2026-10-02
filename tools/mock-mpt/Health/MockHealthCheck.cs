using System.Text;
using NightTales.RenderNode.Server;

namespace NightTales.MockMpt.Health;

/// <summary>
/// The <c>--healthcheck</c> mode of the mock binary (11.1.2-T1 review fix, CODE-1/CODE-2): one authenticated
/// <c>GET /v1/health</c> from inside the container. The probe reads the same key resolution the server uses —
/// <c>MOCK_API_KEY</c>, else <c>RenderNodeServer__ApiKey</c>, else the dev default — so a custom key is probed
/// with the value the server actually bound, and the runtime image no longer needs <c>curl</c>.
/// </summary>
/// <remarks>
/// The key travels as an <c>x-api-key</c> request header, never on the command line (01 D9). The endpoint
/// defaults to <c>http://127.0.0.1:8090/v1/health</c> (Kestrel inside the container reaches its own loopback)
/// and can be overridden with <c>MOCK_HEALTH_URL</c>.
/// </remarks>
public static class MockHealthCheck
{
    /// <summary>The argv flag that selects this mode instead of starting the host.</summary>
    public const string Flag = "--healthcheck";

    /// <summary>The probe URL when <c>MOCK_HEALTH_URL</c> is unset; matches the image's <c>http://+:8090</c>.</summary>
    public const string DefaultUrl = "http://127.0.0.1:8090/v1/health";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>True when the process was started as the probe (<c>dotnet NightTales.MockMpt.dll --healthcheck</c>).</summary>
    public static bool IsRequested(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Contains(Flag, StringComparer.Ordinal);
    }

    /// <summary>Runs the probe; 0 only for a 2xx answer. Failure details go to stderr, never the key.</summary>
    public static async Task<int> RunAsync(Func<string, string?> read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);

        var key = MockMptEnvironment.ResolveApiKey(read);
        if (Encoding.UTF8.GetByteCount(key) < RenderNodeServerOptions.MinimumApiKeyBytes)
        {
            Console.Error.WriteLine(
                $"mock-mpt: --healthcheck needs a MOCK_API_KEY/RenderNodeServer__ApiKey of at least "
                + $"{RenderNodeServerOptions.MinimumApiKeyBytes} bytes");
            return 1;
        }

        var url = read("MOCK_HEALTH_URL");
        var target = string.IsNullOrWhiteSpace(url) ? DefaultUrl : url.Trim();

        try
        {
            using var client = new HttpClient { Timeout = ProbeTimeout };
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            request.Headers.TryAddWithoutValidation(RenderNodeHeaders.ApiKey, key);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return 0;
            }

            Console.Error.WriteLine($"mock-mpt: --healthcheck {target} answered {(int)response.StatusCode}");
            return 1;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine($"mock-mpt: --healthcheck {target} failed: {exception.GetType().Name}");
            return 1;
        }
    }
}
