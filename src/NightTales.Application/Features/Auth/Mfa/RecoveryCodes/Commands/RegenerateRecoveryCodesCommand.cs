using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;

namespace NightTales.Application.Features.Auth.Mfa.RecoveryCodes.Commands;

/// <summary>
/// <c>POST /api/v1/me/mfa/recovery-codes</c> (09.10.4-T1 Design): replace every recovery code and receive
/// the ten new ones once.
/// </summary>
/// <remarks>
/// No body: the set belongs to the caller. The route requires a recent re-authentication
/// (<c>RequireReauth</c>), which is what makes the previous codes stop working only for someone who
/// proved the password and the code again.
/// </remarks>
[NotAudited("RegenerateRecoveryCodesHandler's flow writes the mfa.recovery_codes_regenerated row itself through IAuditWriter, with the caller's IP and User-Agent (09.10.4-T1).")]
public sealed record RegenerateRecoveryCodesCommand : ICommand<ErrorOr<IReadOnlyList<string>>>;
