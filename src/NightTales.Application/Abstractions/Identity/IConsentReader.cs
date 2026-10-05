namespace NightTales.Application.Abstractions.Identity;

/// <summary>Reads latest consent stamps, optionally including this transaction's pending acceptances.</summary>
public interface IConsentReader
{
    /// <summary>Reads both documents in one database round trip, without tracking persisted rows.</summary>
    Task<IReadOnlyList<LegalConsentSnapshot>> LatestAsync(Guid userId, CancellationToken ct, bool includePending = false);
}

/// <summary>A consent projection without the stored IP address.</summary>
public sealed record LegalConsentSnapshot(LegalDocument Document, string Version, DateTimeOffset AcceptedAt);
