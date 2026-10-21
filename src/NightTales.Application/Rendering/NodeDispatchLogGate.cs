namespace NightTales.Application.Rendering;

/// <summary>
/// Lets <see cref="NodeSelector"/> log "nothing dispatchable" at most once per interval (11.3.3-T2 failure
/// handling: an empty health cache in the Worker's first 30 s, or a node-less staging, must not flood the
/// log with one warning per pick). A singleton, because the selector itself is scoped.
/// </summary>
public sealed class NodeDispatchLogGate
{
    private readonly Lock _gate = new();
    private DateTimeOffset? _lastLoggedAt;

    /// <summary>Whether a warning may be written now; <c>true</c> claims the interval.</summary>
    /// <param name="now">The clock's current instant.</param>
    /// <param name="interval">The minimum spacing between two warnings.</param>
    public bool TryClaim(DateTimeOffset now, TimeSpan interval)
    {
        lock (_gate)
        {
            if (_lastLoggedAt is { } last && now >= last && now - last < interval)
            {
                return false;
            }

            _lastLoggedAt = now;
            return true;
        }
    }
}
