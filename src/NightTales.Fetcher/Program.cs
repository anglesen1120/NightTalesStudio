using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightTales.Fetcher;
using NightTales.Fetcher.InternalApi;

// The fetcher host (02.5.2-T3, 02 D10): a console BackgroundService with no listening socket and no health endpoint
// (DECISION T43). Its only secrets-bearing input is FETCHER_API_TOKEN, checked before anything else is built so a
// missing token ends the process well inside two seconds. The message never contains a token value.
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(FetcherApiOptions.TokenVariable))
    && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NTS_" + FetcherApiOptions.TokenVariable)))
{
    await Console.Error.WriteLineAsync($"{FetcherApiOptions.TokenVariable} is not set; the fetcher cannot authenticate to the internal API.");

    return 1;
}

try
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        // appsettings.json travels beside the binary, whatever the working directory of `dotnet run` or the container is.
        ContentRootPath = AppContext.BaseDirectory,
    });
    builder.Configuration.AddEnvironmentVariables("NTS_");

    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(options =>
    {
        options.IncludeScopes = false;
        options.UseUtcTimestamp = true;
        options.TimestampFormat = "o";
    });

    builder.Services.AddFetcherHost(builder.Configuration);
    builder.Services.AddFetcherTelemetry(builder.Configuration);

    using var host = builder.Build();
    await host.RunAsync();

    return Environment.ExitCode;
}
catch (OptionsValidationException ex)
{
    // The failures name configuration keys, never values.
    await Console.Error.WriteLineAsync("fetcher configuration is invalid: " + string.Join(' ', ex.Failures));

    return 1;
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    await Console.Error.WriteLineAsync("fetcher failed to start: " + ex.GetType().Name);

    return 1;
}
