namespace NightTales.Application.Abstractions.Publishing;

/// <summary>
/// One resolved OAuth client of the <c>YouTube</c> section (06 README D5): the client id an
/// <c>oauth_states</c> row pins and the secret the resolver read through the secret provider.
/// </summary>
/// <remarks>
/// The secret travels in the process only: it is never logged, never serialised and never stored —
/// the state row keeps the client <em>id</em> and the secret is re-read at the callback (06 README D3).
/// </remarks>
/// <param name="ClientId">The public OAuth client id (the <c>YouTube:Clients</c> key).</param>
/// <param name="ClientSecret">The client secret, read through <c>ISecretProvider</c>.</param>
public sealed record OAuthClient(string ClientId, string ClientSecret);
