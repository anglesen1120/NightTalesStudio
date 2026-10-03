// The Responses API of OpenAI 2.14.0 is marked experimental (OPENAI001); the adapter under test uses it.
#pragma warning disable OPENAI001

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Flags;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Text;
using NightTales.Domain.Stories.Generation;
using NightTales.Infrastructure.OpenAI;

namespace NightTales.Infrastructure.UnitTests.OpenAI;

/// <summary>One request the scripted transport saw: headers and body as sent (the body is test data only).</summary>
internal sealed record CapturedRequest(HttpMethod Method, Uri? Uri, IReadOnlyDictionary<string, string> Headers, string Body)
{
    internal JsonElement Json => JsonDocument.Parse(Body).RootElement;
}

/// <summary>The scripted HTTP transport under the real <c>"openai"</c> HttpClient pipeline: one handler per queued reply.</summary>
internal sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _script = new();
    private readonly List<CapturedRequest> _requests = [];

    internal IReadOnlyList<CapturedRequest> Requests => _requests;

    internal ScriptedHandler Reply(HttpStatusCode status, string body, params (string Name, string Value)[] headers) =>
        Then(_ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            foreach (var (name, value) in headers)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return Task.FromResult(response);
        });

    internal ScriptedHandler Then(Func<CancellationToken, Task<HttpResponseMessage>> next)
    {
        _script.Enqueue(next);

        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        _requests.Add(new CapturedRequest(request.Method, request.RequestUri, headers, body));

        return _script.Count == 0
            ? throw new InvalidOperationException("ScriptedHandler has no reply left.")
            : await _script.Dequeue()(cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class TestEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Production;

    public string ApplicationName { get; set; } = "test";

    public string ContentRootPath { get; set; } = "/";

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>A flag reader with one switch; fail-closed like the real one.</summary>
internal sealed class FakeFlags : IFeatureFlags
{
    internal bool FundsExhausted { get; set; }

    public Task<bool> IsEnabledAsync(string key, CancellationToken ct) =>
        Task.FromResult(FundsExhausted && key == OpenAiTextClient.FundsExhaustedFlag);
}

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    internal List<string> Messages { get; } = [];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(List<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            messages.Add(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                messages.AddRange(pairs.Select(p => $"{p.Key}={p.Value}"));
            }

            if (exception is not null)
            {
                messages.Add(exception.ToString());
            }
        }
    }
}

/// <summary>The real adapter composition over a scripted transport: the registration code, not a hand-built client.</summary>
internal sealed class OpenAiTestHost : IDisposable
{
    internal const string Model = "gpt-6-luna";

    private readonly ServiceProvider _provider;

    internal OpenAiTestHost(Action<OpenAiOptions>? configure = null)
    {
        Handler = new ScriptedHandler();
        Flags = new FakeFlags();
        Logs = new CapturingLoggerProvider();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging(builder => builder.AddProvider(Logs));
        services.AddSingleton<IFeatureFlags>(Flags);
        services.AddSingleton<IHostEnvironment>(new TestEnvironment());
        services.AddOpenAiTextClient();
        services.Configure<OpenAiOptions>(options =>
        {
            options.ApiKey = "unit-test-api-key-placeholder";
            options.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
            configure?.Invoke(options);
        });
        services.Configure<HttpClientFactoryOptions>(
            OpenAiServiceCollectionExtensions.HttpClientName,
            options => options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = Handler));

        _provider = services.BuildServiceProvider();
        Client = _provider.GetRequiredService<ITextClient>();
    }

    internal ScriptedHandler Handler { get; }

    internal FakeFlags Flags { get; }

    internal CapturingLoggerProvider Logs { get; }

    internal ITextClient Client { get; }

    internal static TextCallRequest Request(
        TextStep step = TextStep.Writer,
        int attempt = 1,
        Guid? userId = null,
        string system = "SYSTEM-PROMPT-SECRET",
        string data = "{\"brief\":\"DATA-OBJECT-SECRET\"}",
        int maxOutput = 4321) =>
        new(
            new RunContext(
                Guid.Parse("0197a000-0000-7000-8000-00000000abcd"),
                userId ?? Guid.Parse("0197a000-0000-7000-8000-000000000001"),
                Guid.NewGuid(),
                Guid.NewGuid(),
                new ReservationId(Guid.NewGuid()),
                GenerationTrigger.User,
                attempt,
                "1"),
            step,
            system,
            data,
            "probe.v1",
            new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(ProbeSchema)),
            maxOutput,
            "writer:ghost.vi.v1",
            "rel=test");

    internal const string ProbeSchema =
        """{"type":"object","additionalProperties":false,"required":["title","count"],"properties":{"title":{"type":"string"},"count":{"type":"integer"}}}""";

    /// <summary>A completed Responses body whose message carries <paramref name="outputText"/>.</summary>
    internal static string Completed(string outputText, string usage = DefaultUsage, string status = "completed", string? incomplete = null) =>
        $$"""
        {"id":"resp_1","object":"response","created_at":1,"status":"{{status}}","model":"{{Model}}",
         {{(incomplete is null ? string.Empty : "\"incomplete_details\":{\"reason\":\"" + incomplete + "\"},")}}
         "output":[{"type":"message","id":"msg_1","status":"completed","role":"assistant","content":[{"type":"output_text","text":{{JsonSerializer.Serialize(outputText)}},"annotations":[]}]}]
         {{(usage.Length == 0 ? string.Empty : ",\"usage\":" + usage)}}}
        """;

    internal const string DefaultUsage =
        """{"input_tokens":100,"input_tokens_details":{"cached_tokens":20},"output_tokens":50,"output_tokens_details":{"reasoning_tokens":10},"total_tokens":150}""";

    internal static string Refusal() =>
        """
        {"id":"resp_1","object":"response","created_at":1,"status":"completed","model":"gpt-6-luna",
         "output":[{"type":"message","id":"msg_1","status":"completed","role":"assistant","content":[{"type":"refusal","refusal":"REFUSAL-TEXT"}]}],
         "usage":{"input_tokens":100,"output_tokens":5,"total_tokens":105}}
        """;

    internal static string Error(string code) => "{\"error\":{\"message\":\"m\",\"type\":\"t\",\"code\":\"" + code + "\"}}";

    public void Dispose() => _provider.Dispose();
}
