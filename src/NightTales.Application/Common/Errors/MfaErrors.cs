using ErrorOr;
using NightTales.Application.Common.Errors;

namespace NightTales.Application.Common.Errors;

/// <summary>
/// The MFA and re-authentication error factories (09.10.4-T1): the codes of 09 §5.4 this story answers
/// with, spelled once so the handlers, the flows and the Api filters cannot drift apart.
/// </summary>
/// <remarks>
/// The HTTP status still comes from the code catalogue (<c>Api/Http/ErrorCatalogue.cs</c>), never from
/// <see cref="ErrorType"/> (09 README D7): <c>reauth_required</c> and <c>invalid_mfa_code</c> are 401 and
/// <c>rate_limited</c> is 429.
/// </remarks>
public static class MfaErrors
{
    /// <summary>
    /// 401 <c>reauth_required</c>: the route needs a re-authentication no older than the window (BD4).
    /// </summary>
    public static Error ReauthRequired() =>
        Error.Unauthorized(ErrorCodes.ReauthRequired, "Re-authentication is required.");

    /// <summary>
    /// 401 <c>unauthorized</c>: the defensive answer of a <c>/me/*</c> handler whose caller has no
    /// <c>sub</c> claim. The default-deny authorization behaviour answers it first (09 README D4/D10), so
    /// this only ever fires if that ordering changes.
    /// </summary>
    public static Error Unauthenticated() =>
        Error.Unauthorized(ErrorCodes.Unauthorized, "Authentication is required.");

    /// <summary>401 <c>invalid_mfa_code</c>: the submitted authenticator or recovery code is not accepted.</summary>
    /// <param name="description">The client-safe reason; never names the secret or the expected code.</param>
    public static Error InvalidMfaCode(string description) =>
        Error.Unauthorized(ErrorCodes.InvalidMfaCode, description);

    /// <summary>
    /// 401 <c>mfa_token_invalid</c> (09.3.2-T3): the presented <c>mfaToken</c> or pre-auth cookie names
    /// no usable challenge — it is unknown, already consumed, expired, presented with another
    /// <c>User-Agent</c>, or the account is gone (BD4).
    /// </summary>
    /// <remarks>
    /// Every one of those cases answers the same code and the same sentence on purpose: distinguishing
    /// them would tell a caller which half of the pair was wrong, and the challenge is a bearer secret.
    /// </remarks>
    public static Error MfaTokenInvalid() =>
        Error.Unauthorized(ErrorCodes.MfaTokenInvalid, "The sign-in challenge is no longer valid; sign in again.");

    /// <summary>
    /// 429 <c>rate_limited</c> with the <c>Retry-After</c> the writer mirrors from
    /// <see cref="ErrorMetadata.RetryAfterSeconds"/> ([RULED — README §B10 WT35]).
    /// </summary>
    /// <param name="retryAfterSeconds">The seconds the client must wait (BD4/WT35: 900).</param>
    public static Error RateLimited(int retryAfterSeconds) =>
        Error.Failure(
            code: ErrorCodes.RateLimited,
            description: "Too many wrong codes; try again later.",
            metadata: new Dictionary<string, object>
            {
                [ErrorMetadata.RetryAfterSeconds] = retryAfterSeconds,
            });
}
