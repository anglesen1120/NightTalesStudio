namespace NightTales.Api.Contracts.Publishing;

/// <summary>
/// The body of <c>POST /api/v1/publications/{id}/consent</c> (06.2.2-T1 task §Design, 06 README D7): the version
/// of the consent text the owner was actually shown, which must be the live <c>YouTube:ConsentTextVersion</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One member on purpose.</b> The publication is the route value and the owner is the session, so neither is a
/// body member; the header (<c>If-Match</c>) is bound by the route, not the body. A client therefore cannot name a
/// consent source, an instant, a status or another owner's row — the handler derives all of them (06 README D17).
/// </para>
/// <para>
/// <b>Two checks, two places.</b> The shape (non-empty, at most 32 characters) is the command validator's 400
/// <c>validation_failed</c>. Whether the value is the <em>current</em> text is the domain's rule and answers 400
/// <c>consent_text_outdated</c> under field <c>consentTextVersion</c>, so a client that rendered an older text is
/// told to refetch rather than being told its body was malformed (D7).
/// </para>
/// </remarks>
/// <param name="ConsentTextVersion">
/// The version the client displayed, for example <c>pc.v1</c> — the same string
/// <c>GET /api/v1/publications/consent-text</c> answered in its <c>version</c> member.
/// </param>
public sealed record ConsentPublicationRequest(string ConsentTextVersion);
