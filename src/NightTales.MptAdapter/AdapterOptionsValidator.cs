using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NightTales.MptAdapter.Tls;

namespace NightTales.MptAdapter;

/// <summary>
/// The <c>ValidateOnStart</c> rules of <see cref="AdapterOptions"/> (11.3.5-T4, D6): the loopback-only MPT URL
/// (<see cref="AdapterOptions.ParseLoopbackMptBaseUrl"/>, which also refuses a non-root path, QA89-LB-01), the
/// private listener address (loopback only in Development, T44) and positive ports/intervals.
/// </summary>
/// <remarks>Messages name the key and never the configured value.</remarks>
public sealed class AdapterOptionsValidator(IHostEnvironment environment) : IValidateOptions<AdapterOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AdapterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        try
        {
            AdapterOptions.ParseLoopbackMptBaseUrl(options.MptBaseUrl);
        }
        catch (ArgumentException exception)
        {
            failures.Add(exception.Message);
        }
        catch (UriFormatException)
        {
            failures.Add("Adapter:MptBaseUrl is not a valid absolute URL.");
        }

        if (TlsPrivateAddressGuard.Check(options.ListenAddress, environment.IsDevelopment()) is { } listenFailure)
        {
            failures.Add(listenFailure);
        }

        Require(failures, options.Port is >= 1 and <= 65535, "Adapter:Port must be 1-65535.");
        Require(failures, options.PollSeconds >= 1, "Adapter:PollSeconds must be at least 1.");
        Require(failures, options.CheckConcurrency >= 1, "Adapter:CheckConcurrency must be at least 1.");
        Require(failures, options.CheckTimeoutSeconds >= 1, "Adapter:CheckTimeoutSeconds must be at least 1.");
        Require(failures, options.AssetsRefreshSeconds >= 1, "Adapter:AssetsRefreshSeconds must be at least 1.");
        Require(failures, options.GpuSampleSeconds >= 1, "Adapter:GpuSampleSeconds must be at least 1.");
        Require(failures, !string.IsNullOrWhiteSpace(options.DataDir), "Adapter:DataDir must be set.");
        Require(failures, !string.IsNullOrWhiteSpace(options.VideoCodec), "Adapter:VideoCodec must be set.");
        Require(
            failures,
            Uri.TryCreate(options.ProxyHealthUrl, UriKind.Absolute, out var proxy) && proxy.Scheme == Uri.UriSchemeHttp,
            "Adapter:ProxyHealthUrl must be an absolute http URL.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Require(List<string> failures, bool condition, string message)
    {
        if (!condition)
        {
            failures.Add(message);
        }
    }
}
