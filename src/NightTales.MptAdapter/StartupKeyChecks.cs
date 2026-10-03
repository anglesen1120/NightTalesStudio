using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using NightTales.MptAdapter.Tls;

namespace NightTales.MptAdapter;

/// <summary>
/// The fatal startup checks of 11.3.5-T4 (exit 2, fixed messages, run before any listener opens): the
/// adapter key must exist and differ from every other key on the node (05 D15 S3), the listener address must be
/// private, the certificate loadable and the data dir writable. Non-fatal conditions (ffmpeg, <c>nvidia-smi</c>,
/// relay, <c>config_mismatch</c>) are never checked here: they surface as health reasons.
/// </summary>
/// <remarks>A message names the setting and never a value, so a key can never reach a log.</remarks>
public static class StartupKeyChecks
{
    public const int ExitCode = 2;

    public const string MissingKeyMessage =
        "RenderNodeServer:ApiKey (ADAPTER_API_KEY) is not set: the adapter refuses to start without its key.";

    public const string KeyEqualsMptKeyMessage = "ADAPTER_API_KEY must differ from NTS_MPT_API_KEY (S3).";
    public const string KeyEqualsProxyKeyMessage = "ADAPTER_API_KEY must differ from NTS_PROXY_API_KEY (S3).";
    public const string KeyEqualsImageKeyMessage = "ADAPTER_API_KEY must differ from NODE_OPENAI_IMAGE_KEY (S3).";
    public const string KeyEqualsNextMessage = "ADAPTER_API_KEY must differ from ADAPTER_API_KEY_NEXT (S3).";
    public const string NextEqualsMptKeyMessage = "ADAPTER_API_KEY_NEXT must differ from NTS_MPT_API_KEY (S3).";
    public const string NextEqualsProxyKeyMessage = "ADAPTER_API_KEY_NEXT must differ from NTS_PROXY_API_KEY (S3).";
    public const string NextEqualsImageKeyMessage = "ADAPTER_API_KEY_NEXT must differ from NODE_OPENAI_IMAGE_KEY (S3).";
    public const string DataDirMessage = "Adapter:DataDir is not writable.";

    /// <summary>
    /// Env names a key may not equal (S3), with the fixed message of the node key and of the rotation (NEXT)
    /// key for each (T68: NEXT is a valid presented key, so it is held to the same distinctness).
    /// </summary>
    private static readonly (string Variable, string Message, string NextMessage)[] OtherKeys =
    [
        ("NTS_MPT_API_KEY", KeyEqualsMptKeyMessage, NextEqualsMptKeyMessage),
        ("NTS_PROXY_API_KEY", KeyEqualsProxyKeyMessage, NextEqualsProxyKeyMessage),
        ("NODE_OPENAI_IMAGE_KEY", KeyEqualsImageKeyMessage, NextEqualsImageKeyMessage),
    ];

    /// <summary>The first fatal message, or null when the adapter may start.</summary>
    public static string? Run(
        IConfiguration configuration,
        Func<string, string?> environment,
        bool isDevelopment,
        TimeProvider clock,
        out LoadedCertificate? certificate)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(clock);

        certificate = null;

        if (CheckKeys(configuration, environment) is { } keyFailure)
        {
            return keyFailure;
        }

        var options = new AdapterOptions();
        configuration.GetSection(AdapterOptions.SectionName).Bind(options);

        if (TlsPrivateAddressGuard.Check(options.ListenAddress, isDevelopment) is { } listenFailure)
        {
            return listenFailure;
        }

        if (TlsValidator.TryLoad(options.CertPath, options.KeyPath, clock, out certificate) is { } tlsFailure)
        {
            return tlsFailure;
        }

        if (!IsWritable(options.DataDir))
        {
            certificate?.Certificate.Dispose();
            certificate = null;
            return DataDirMessage;
        }

        return null;
    }

    /// <summary>The key rules alone: missing key, then equality with NEXT and the three other node keys.</summary>
    public static string? CheckKeys(IConfiguration configuration, Func<string, string?> environment)
    {
        var key = Value(configuration["RenderNodeServer:ApiKey"], environment("ADAPTER_API_KEY"));
        if (key is null)
        {
            return MissingKeyMessage;
        }

        var next = Value(configuration["RenderNodeServer:ApiKeyNext"], environment("ADAPTER_API_KEY_NEXT"));
        if (next is not null && SameSecret(key, next))
        {
            return KeyEqualsNextMessage;
        }

        foreach (var (variable, message, nextMessage) in OtherKeys)
        {
            if (Value(environment(variable), null) is not { } other)
            {
                continue;
            }

            if (SameSecret(key, other))
            {
                return message;
            }

            if (next is not null && SameSecret(next, other))
            {
                return nextMessage;
            }
        }

        return null;
    }

    private static string? Value(string? primary, string? fallback) =>
        !string.IsNullOrEmpty(primary) ? primary : string.IsNullOrEmpty(fallback) ? null : fallback;

    private static bool SameSecret(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-check-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
