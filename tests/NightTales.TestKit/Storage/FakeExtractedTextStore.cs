using ErrorOr;
using NightTales.Application.Abstractions.Storage;

namespace NightTales.TestKit.Storage;

/// <summary>In-memory extracted texts with a scripted storage outage; never contacts object storage.</summary>
public sealed class FakeExtractedTextStore : IExtractedTextStore
{
    private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);

    /// <summary>Whether reads return the storage port's service-unavailable error.</summary>
    public bool FailReads { get; set; }

    /// <summary>The number of attempted reads.</summary>
    public int ReadCalls { get; private set; }

    /// <summary>Seeds extracted text under a known object key.</summary>
    public void Add(string key, string text) => _texts.Add(key, text);

    /// <inheritdoc />
    public Task<ErrorOr<string>> WriteAsync(
        Guid userId, Guid episodeId, Guid sourceId, string sha256, string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = $"research/{userId}/{episodeId}/{sourceId}/{sha256}.txt";
        _texts[key] = text;
        return Task.FromResult<ErrorOr<string>>(key);
    }

    /// <inheritdoc />
    public Task<ErrorOr<string>> ReadAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCalls++;
        if (FailReads)
        {
            return Task.FromResult<ErrorOr<string>>(Error.Failure("service_unavailable", "Extracted text storage is unavailable."));
        }

        return Task.FromResult<ErrorOr<string>>(_texts.TryGetValue(key, out var text)
            ? text
            : Error.NotFound("not_found", "Extracted text does not exist."));
    }
}
