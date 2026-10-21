namespace NightTales.Domain.Common;

/// <summary>Lifecycle state of an account (00 overview §6 <c>users.status</c>).</summary>
public enum UserStatus
{
    Active,
    Suspended,
    Deleted,
}
