using System.Net;
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Audit;
using NightTales.Application.Abstractions.Identity;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Audit;

namespace NightTales.Application.Features.Account.Legal.Commands.AcceptLegalDocuments;

/// <summary>Accepts both rendered versions atomically for the authenticated caller.</summary>
[Audited("account.legal_accepted", Category.AccountSecurity, Visibility.User)]
public sealed record AcceptLegalDocumentsCommand(string Tos, string Privacy)
    : ICommand<ErrorOr<LegalAcceptanceDto>>, IAuditData
{
    /// <inheritdoc />
    public AuditData ToAuditData() => new(null, "user", null,
        new Dictionary<string, object> { ["tos"] = Tos, ["privacy"] = Privacy });
}

/// <summary>Appends the server's versions; the pipeline commits consents and audit together.</summary>
public sealed class AcceptLegalDocumentsHandler(
    ICurrentUser currentUser,
    ICurrentLegalVersions versions,
    IConsentWriter writer,
    IRequestInfoAccessor requestInfo,
    LegalAcceptanceReader reader) : ICommandHandler<AcceptLegalDocumentsCommand, ErrorOr<LegalAcceptanceDto>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<LegalAcceptanceDto>> Handle(
        AcceptLegalDocumentsCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.Id is not { } userId)
        {
            return MfaErrors.Unauthenticated();
        }
        var tos = versions.GetCurrentVersion(LegalDocument.Tos);
        var privacy = versions.GetCurrentVersion(LegalDocument.Privacy);
        List<string> outdated = [];
        if (command.Privacy != privacy)
        {
            outdated.Add("privacy");
        }
        if (command.Tos != tos)
        {
            outdated.Add("tos");
        }
        if (outdated.Count != 0)
        {
            return Error.Conflict(ErrorCodes.LegalVersionOutdated,
                "You are accepting an outdated document version. Reload the documents and try again.",
                new Dictionary<string, object> { ["documents"] = outdated.ToArray() });
        }
        var ip = IPAddress.TryParse(requestInfo.Current.Ip, out var parsed) ? parsed : null;
        var terms = await writer.RecordAsync(userId, LegalDocument.Tos, tos, ip, cancellationToken).ConfigureAwait(false);
        if (terms.IsError)
        {
            return terms.Errors;
        }
        var policy = await writer.RecordAsync(userId, LegalDocument.Privacy, privacy, ip, cancellationToken).ConfigureAwait(false);
        if (policy.IsError)
        {
            return policy.Errors;
        }
        return await reader.ReadAsync(userId, cancellationToken, includePending: true).ConfigureAwait(false);
    }
}
