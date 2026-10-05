using ErrorOr;
using NightTales.Application.Abstractions.Compliance;
using NightTales.Application.Common.Errors;

namespace NightTales.Application.Features.Account.Legal;

/// <summary>The read-only, fail-closed gate shared by connect and autopilot.</summary>
public sealed class LegalAcceptanceService(LegalAcceptanceReader reader) : ILegalAcceptance
{
    /// <inheritdoc />
    public async Task<ErrorOr<Success>> EnsureCurrentAsync(Guid userId, CancellationToken ct)
    {
        var acceptance = await reader.ReadAsync(userId, ct).ConfigureAwait(false);
        var missing = acceptance.Documents
            .Where(document => document.AcceptedVersion != document.CurrentVersion)
            .Select(document => document.Document)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return missing.Length == 0
            ? Result.Success
            : Error.Conflict(ErrorCodes.LegalAcceptanceRequired, "Legal acceptance required.",
                new Dictionary<string, object> { ["documents"] = missing });
    }
}
