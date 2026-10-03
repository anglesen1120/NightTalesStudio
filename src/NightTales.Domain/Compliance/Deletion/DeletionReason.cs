namespace NightTales.Domain.Compliance.Deletion;

/// <summary>Why a deletion duty exists; it fixes the legal deadline (7 d, or 30 d for <see cref="GoogleRevoke"/>).</summary>
public enum DeletionReason
{
    UserRequest,
    InAppRevoke,
    GoogleRevoke,
    AccountDelete,
}
