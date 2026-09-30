namespace NightTales.Application.Abstractions.Identity;

/// <summary>
/// A legal document whose acceptance a registration records (13 FR-13-2, 09 README BD1). The members
/// are stored snake_case (<c>user_consents.document</c>: <c>tos</c> | <c>privacy</c>) by the
/// <c>HasEnumCheck</c> convention of 09.2.1-T1.
/// </summary>
/// <remarks>
/// The name is an [INFERENCE] of 09.3-auth/plan.md §3 (<c>LegalDocument { Tos, Privacy }</c>); 13's
/// <c>age_18</c> attestation arrives with 13.2.3-T1 (13 CR-11) and extends this enum, not a copy.
/// </remarks>
public enum LegalDocument
{
    /// <summary>Terms of service.</summary>
    Tos,

    /// <summary>Privacy policy.</summary>
    Privacy,
}
