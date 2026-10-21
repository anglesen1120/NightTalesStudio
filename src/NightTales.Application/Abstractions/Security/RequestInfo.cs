namespace NightTales.Application.Abstractions.Security;

/// <summary>
/// The untrusted caller metadata a security-relevant write records: the remote address and the
/// <c>User-Agent</c> (09 README BD3/BD4 — <c>refresh_tokens.ip</c>/<c>user_agent</c>, the audit rows of
/// 09.3.6-T1 and the consent rows of 09.3.1-T1).
/// </summary>
/// <remarks>
/// Declared by 09.3.1-T1's <c>IRegistrationFlow</c> (which takes it) ahead of its signed home,
/// 09.3.2-T1's <c>Application/Abstractions/Security/IRefreshTokenService.cs</c>: the record is the
/// same one, so 09.3.2-T1 reuses this type instead of declaring a second copy. Both fields are
/// nullable because neither is guaranteed on every request; they are never logged (09 README D17).
/// </remarks>
/// <param name="Ip">The caller's address as text, or null when it cannot be resolved.</param>
/// <param name="UserAgent">The caller's <c>User-Agent</c> header, or null.</param>
public sealed record RequestInfo(string? Ip, string? UserAgent);
