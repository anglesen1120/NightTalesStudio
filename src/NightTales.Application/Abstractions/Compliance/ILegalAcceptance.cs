using ErrorOr;

namespace NightTales.Application.Abstractions.Compliance;

/// <summary>Checks current legal acceptance without writing or allowing access on a read failure.</summary>
public interface ILegalAcceptance
{
    /// <summary>Requires acceptance of both current documents for the given user.</summary>
    Task<ErrorOr<Success>> EnsureCurrentAsync(Guid userId, CancellationToken ct);
}
