namespace NightTales.Api.Contracts.Publishing;

/// <summary>
/// The body of <c>POST /api/v1/publications/cancel-by-link</c> (06.2.2-T2 §Design "Cancel-by-link").
/// </summary>
/// <remarks>
/// <para>
/// Two values, both from the veto-notice e-mail: the publication the mail was about and the token the URL
/// fragment carried. The route is anonymous, so nothing else identifies the caller — the token is the
/// authentication, and <c>publicationId</c> is only compared with the subject the token was minted for, never
/// used to find the row (the token owner's id does that).
/// </para>
/// <para>
/// The token is bound as a plain <see cref="string"/> so an absent or malformed value reaches the handler and
/// answers the route's single <c>400 cancel_token_invalid</c>, rather than a binder-level
/// <c>validation_failed</c> that would be a second, distinguishable refusal.
/// </para>
/// </remarks>
/// <param name="PublicationId">The publication (<c>publications.id</c>) the link named.</param>
/// <param name="Token">The plaintext single-use token from the link.</param>
public sealed record CancelByLinkRequest(Guid PublicationId, string Token);
