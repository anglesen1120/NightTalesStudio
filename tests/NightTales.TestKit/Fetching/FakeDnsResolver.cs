using System.Collections.Concurrent;
using System.Net;
using NightTales.Domain.Research;

namespace NightTales.TestKit.Fetching;

/// <summary>
/// Scripted <see cref="IDnsResolver"/> (02 D20): answers each host from a map, falls back to
/// <see cref="DefaultAnswer"/>, and records every lookup so a test can prove how often (and for which host) the name was
/// resolved.
/// </summary>
public sealed class FakeDnsResolver : IDnsResolver
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<IPAddress>> _answers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _lookups = new();

    /// <summary>The answer for a host with no entry; empty (no records) by default.</summary>
    public IReadOnlyList<IPAddress> DefaultAnswer { get; set; } = [];

    /// <summary>The hosts looked up, in call order.</summary>
    public IReadOnlyList<string> Lookups => [.. _lookups];

    /// <summary>Answers <paramref name="host"/> with <paramref name="addresses"/>.</summary>
    /// <param name="host">The host name.</param>
    /// <param name="addresses">The addresses, in the order the resolver returns them.</param>
    public FakeDnsResolver Map(string host, params IPAddress[] addresses)
    {
        _answers[host] = addresses;

        return this;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _lookups.Enqueue(host);

        return ValueTask.FromResult(_answers.TryGetValue(host, out var answer) ? answer : DefaultAnswer);
    }
}
