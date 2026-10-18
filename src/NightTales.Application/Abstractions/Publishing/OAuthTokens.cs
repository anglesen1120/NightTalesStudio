namespace NightTales.Application.Abstractions.Publishing;

/// <summary>
/// One Google token response (06 README D2): the access token, the refresh token of an offline grant, its
/// absolute expiry and the granted scope list.
/// </summary>
/// <remarks>
/// The access token and the refresh token are secrets of the connected account: they are handed to the
/// channel-connection writer in memory and protected there (<c>tokens.v1/{connectionId}</c>), never logged
/// and never serialised (06 README D17).
/// </remarks>
/// <param name="AccessToken">The bearer token for the YouTube Data API calls.</param>
/// <param name="RefreshToken">
/// The long-lived grant, present because the connect sends <c>access_type=offline</c> +
/// <c>prompt=consent</c>; a response without one cannot keep the connection usable after an hour and is
/// treated as a failed exchange.
/// </param>
/// <param name="ExpiresAt">When <paramref name="AccessToken"/> stops being valid.</param>
/// <param name="Scope">The scope list Google echoed, space-joined.</param>
public sealed record OAuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, string Scope);
