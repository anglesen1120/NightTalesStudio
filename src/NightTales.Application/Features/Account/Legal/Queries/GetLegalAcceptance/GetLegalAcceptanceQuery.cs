using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions;
using NightTales.Application.Common.Errors;

namespace NightTales.Application.Features.Account.Legal.Queries.GetLegalAcceptance;

/// <summary>Reads the authenticated caller's legal acceptance.</summary>
public sealed record GetLegalAcceptanceQuery : IQuery<ErrorOr<LegalAcceptanceDto>>;

/// <summary>Projects acceptance for the caller only.</summary>
public sealed class GetLegalAcceptanceHandler(ICurrentUser currentUser, LegalAcceptanceReader reader)
    : IQueryHandler<GetLegalAcceptanceQuery, ErrorOr<LegalAcceptanceDto>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<LegalAcceptanceDto>> Handle(GetLegalAcceptanceQuery query, CancellationToken cancellationToken)
    {
        if (currentUser.Id is not { } userId)
        {
            return MfaErrors.Unauthenticated();
        }
        return await reader.ReadAsync(userId, cancellationToken).ConfigureAwait(false);
    }
}
