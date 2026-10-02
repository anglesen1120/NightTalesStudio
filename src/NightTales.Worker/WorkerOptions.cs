using System.Net;

namespace NightTales.Worker;

/// <summary>
/// The Worker's own configuration, bound from the <c>Worker</c> section (09.2.6-T1 Outputs).
/// </summary>
public sealed class WorkerOptions
{
    /// <summary>The configuration section this type binds to.</summary>
    public const string SectionName = "Worker";

    /// <summary>The health listener port when <see cref="HealthPort"/> is not set (09.2.6 plan §7).</summary>
    public const int DefaultHealthPort = 8082;

    /// <summary>
    /// The default bind address of the health listener: loopback, so the native dev stack
    /// (<c>tools/dev/up.sh</c>) never exposes it. The optional Compose service overrides it to
    /// <c>0.0.0.0</c> because a published host port reaches the container's interface, not its
    /// loopback, while the published host side stays <c>127.0.0.1</c>.
    /// </summary>
    public const string DefaultHealthAddress = "127.0.0.1";

    /// <summary>The port of the Kestrel health listener.</summary>
    public int HealthPort { get; set; } = DefaultHealthPort;

    /// <summary>
    /// The IP literal the health listener binds; loopback by default. A blank value falls back to
    /// <see cref="DefaultHealthAddress"/>; anything that is not an IP literal is rejected at start-up.
    /// </summary>
    public string HealthAddress { get; set; } = DefaultHealthAddress;

    /// <summary>Resolves the configured health bind address, rejecting anything that is not an IP literal.</summary>
    /// <param name="configured">The raw <c>Worker:HealthAddress</c> value, or null.</param>
    /// <exception cref="InvalidOperationException">The value is set but not a valid IP address.</exception>
    public static IPAddress ResolveHealthAddress(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return IPAddress.Parse(DefaultHealthAddress);
        }

        if (!IPAddress.TryParse(configured.Trim(), out var address))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(HealthAddress)} '{configured}' is not a valid IP address");
        }

        return address;
    }
}
