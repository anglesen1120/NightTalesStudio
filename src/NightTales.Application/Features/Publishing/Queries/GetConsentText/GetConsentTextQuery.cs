using ErrorOr;
using Mediator;

namespace NightTales.Application.Features.Publishing.Queries.GetConsentText;

/// <summary>
/// Reads the publication-consent text a client must display before it may consent (06 README D7; 10 CR-17's
/// <c>GET /api/v1/publications/consent-text?locale=vi|en</c>): the embedded <c>pc.v1</c> markdown of one locale
/// together with the live <c>YouTube:ConsentTextVersion</c> the client sends back to
/// <c>POST /api/v1/publications/{id}/consent</c> (06.2.2-T1).
/// </summary>
/// <param name="Locale">
/// The wire locale, exactly <c>vi</c> or <c>en</c> (ordinal, lower-case — the values 10.7.2-T1 sends). Any other
/// value, including an empty one, is a 400 <c>validation_failed</c> on the <c>locale</c> field: no resource name is
/// ever built from unvalidated input.
/// </param>
public sealed record GetConsentTextQuery(string Locale) : IQuery<ErrorOr<ConsentTextDto>>;
