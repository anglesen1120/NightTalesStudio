using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NightTales.MptAdapter;

/// <summary>
/// D6's second lost rule: the MPT service's start time, compared against the task's <c>backend_started_at</c>.
/// The value is read at adapter start and re-read after every MPT connection error, so a restart that happened
/// while the adapter was not looking is still detected.
/// </summary>
public interface IMptProcessStartReader
{
    /// <summary>The last known MPT start time, or null when the host has no readable systemd (rule disabled).</summary>
    ValueTask<DateTimeOffset?> ReadAsync(CancellationToken cancellationToken);

    /// <summary>Re-runs the probe; the poller calls it after an MPT connection error.</summary>
    ValueTask<DateTimeOffset?> RefreshAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reads <c>systemctl show &lt;unit&gt; -p ExecMainStartTimestamp</c> with an argument list (never a shell, S2)
/// and parses systemd's human timestamp. A null result disables the restart rule and is logged once; the 404
/// half of D6's lost rule keeps working either way, so a node without a readable systemd still reports
/// <c>render_lost</c> for a vanished task.
/// </summary>
public sealed class MptProcessStartReader : IMptProcessStartReader
{
    /// <summary>The unit D8 installs; overridable through <c>Adapter:MptServiceName</c>.</summary>
    public const string DefaultServiceName = "mpt.service";

    /// <summary>The systemd property carrying the process start (D6).</summary>
    public const string TimestampProperty = "ExecMainStartTimestamp";

    /// <summary>The date shape systemd prints.</summary>
    private const string IsoDate = "yyyy-MM-dd";

    /// <summary>The time shape systemd prints.</summary>
    private const string IsoTime = "HH:mm:ss";

    private readonly string _serviceName;
    private readonly ILogger<MptProcessStartReader> _log;
    private DateTimeOffset? _cached;
    private bool _refreshed;
    private bool _unavailableLogged;

    public MptProcessStartReader(IOptions<AdapterOptions> options, ILogger<MptProcessStartReader> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        _serviceName = options.Value.MptServiceName is { Length: > 0 } name ? name : DefaultServiceName;
        _log = log;
    }

    /// <inheritdoc />
    public async ValueTask<DateTimeOffset?> ReadAsync(CancellationToken cancellationToken)
    {
        // The first call reads the process start (D6: "read at start"); later ones serve the cache until an
        // MPT connection error asks for a refresh.
        return _refreshed ? _cached : await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<DateTimeOffset?> RefreshAsync(CancellationToken cancellationToken)
    {
        _refreshed = true;
        var value = await ProbeAsync(cancellationToken).ConfigureAwait(false);

        if (value is null)
        {
            _cached = null;
            if (!_unavailableLogged)
            {
                _unavailableLogged = true;
                _log.ProcessStartUnavailable(TimestampProperty, _serviceName);
            }

            return null;
        }

        _cached = value;
        return value;
    }

    /// <summary>
    /// Parses one line of <c>systemctl show</c> output. Public so the shape systemd prints
    /// (<c>Thu 2026-09-25 09:59:58 UTC</c>) stays covered without a systemd on the test host.
    /// </summary>
    public static DateTimeOffset? ParseTimestamp(string systemctlOutput)
    {
        ArgumentNullException.ThrowIfNull(systemctlOutput);

        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

        foreach (var line in systemctlOutput.Split('\n'))
        {
            var trimmed = line.Trim();
            var separator = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var value = trimmed[(separator + 1)..].Trim();
            if (value.Length == 0 || string.Equals(value, "n/a", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return ParseInstant(value)
                ?? (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, styles, out var parsed)
                    ? parsed
                    : null);
        }

        return null;
    }

    /// <summary>
    /// systemd prints <c>&lt;weekday&gt; &lt;yyyy-MM-dd&gt; &lt;HH:mm:ss&gt;[ &lt;zone&gt;]</c>. Only the date and
    /// the time are read — the day name is decoration and the zone of a node unit is UTC (D8) — so the parse does
    /// not depend on a culture's day names.
    /// </summary>
    private static DateTimeOffset? ParseInstant(string value)
    {
        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index + 1 < tokens.Length; index++)
        {
            if (DateOnly.TryParseExact(tokens[index], IsoDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                && TimeOnly.TryParseExact(tokens[index + 1], IsoTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                return new DateTimeOffset(date, time, TimeSpan.Zero);
            }
        }

        return null;
    }

    private async Task<DateTimeOffset?> ProbeAsync(CancellationToken cancellationToken)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "systemctl",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("show");
        startInfo.ArgumentList.Add(_serviceName);
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(TimestampProperty);

        try
        {
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0 ? ParseTimestamp(output) : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var reason = exception.GetType().Name;
            _log.SystemctlProbeFailed(reason);
            return null;
        }
    }
}
