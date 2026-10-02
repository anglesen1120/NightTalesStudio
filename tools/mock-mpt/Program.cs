using Microsoft.Extensions.Options;
using NightTales.MockMpt;
using NightTales.MockMpt.Health;
using NightTales.RenderNode.Server;

// The mock render node (11.1.2-T1): the real `render-node.v1` surface over an in-memory backend, started
// natively by tools/dev/up.sh on 127.0.0.1:8090 and used as the optional compose service in CI. It reads its
// own MOCK_* environment, validates it, and exits 2 with a named message when a value or a prerequisite is
// unusable — a listener never opens on a configuration nobody can use.

// `--healthcheck` is the container probe (review fix CODE-1/CODE-2): one authenticated GET /v1/health with the
// same key resolution the server uses, so the runtime image needs no curl and a custom key is probed
// correctly. It never opens a listener and exits 0/1.
if (MockHealthCheck.IsRequested(args))
{
    return await MockHealthCheck.RunAsync(Environment.GetEnvironmentVariable, CancellationToken.None);
}

if (!MockMptEnvironment.TryRead(
        Environment.GetEnvironmentVariable,
        AppContext.BaseDirectory,
        out var options,
        out var error))
{
    Console.Error.WriteLine($"mock-mpt: {error}");
    return 2;
}

var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
if (string.IsNullOrWhiteSpace(urls))
{
    // Loopback-only by default (11 README T10/D14): the container overrides this with `http://+:8090`.
    urls = "http://127.0.0.1:8090";
}

var app = Build(options, urls);
if (app is null)
{
    return 2;
}

await using (app)
{
    await app.RunAsync();
}

return 0;

static WebApplication? Build(MockMptOptions options, string urls)
{
    try
    {
        var application = MockHost.Create(options, urls);

        // Force the bound core options through their validator now, so a short key or a bad capacity is an
        // exit 2 at start (05 D15 S3) instead of a 500 on the first request.
        _ = application.Services.GetRequiredService<IOptions<RenderNodeServerOptions>>().Value;
        return application;
    }
    catch (MockStartupException exception)
    {
        Console.Error.WriteLine($"mock-mpt: {exception.Message}");
        return null;
    }
    catch (OptionsValidationException exception)
    {
        Console.Error.WriteLine($"mock-mpt: {exception.Message}");
        return null;
    }
}
