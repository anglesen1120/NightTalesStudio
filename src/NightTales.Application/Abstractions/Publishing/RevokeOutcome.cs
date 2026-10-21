namespace NightTales.Application.Abstractions.Publishing;

/// <summary>
/// How Google answered a token revocation (06 README D9, 06.4.1-T1): declaring it here lets the disconnect
/// flow treat an idempotent retry as success without a second interface.
/// </summary>
public enum RevokeOutcome
{
    /// <summary>Google revoked the grant.</summary>
    Revoked,

    /// <summary>
    /// Google answered a <c>4xx</c> <c>invalid_token</c>: the grant was already gone, which is the same
    /// end state a revocation reaches, so a retry after a competing revocation is a success.
    /// </summary>
    AlreadyRevoked,
}
