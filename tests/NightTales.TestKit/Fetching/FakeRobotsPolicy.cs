using NightTales.Domain.Research;

namespace NightTales.TestKit.Fetching;

/// <summary>Scripted <see cref="IRobotsPolicy"/>: one fixed decision, every checked URL recorded.</summary>
/// <param name="decision">The decision to return; allow with the 1 s floor by default.</param>
public sealed class FakeRobotsPolicy(RobotsDecision? decision = null) : IRobotsPolicy
{
    private readonly List<Uri> _checked = [];

    /// <summary>The decision returned for every URL.</summary>
    public RobotsDecision Decision { get; set; } = decision ?? new RobotsDecision(true, null, TimeSpan.FromSeconds(1));

    /// <summary>The URLs checked, in call order.</summary>
    public IReadOnlyList<Uri> Checked
    {
        get
        {
            lock (_checked)
            {
                return [.. _checked];
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<RobotsDecision> IsAllowedAsync(Uri url, CancellationToken ct)
    {
        lock (_checked)
        {
            _checked.Add(url);
        }

        return ValueTask.FromResult(Decision);
    }
}
