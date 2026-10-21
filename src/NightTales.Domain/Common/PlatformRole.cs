namespace NightTales.Domain.Common;

/// <summary>
/// Platform-wide role of an authenticated user (09 README D16). Stored as snake_case text (D8).
/// </summary>
public enum PlatformRole
{
    User,
    Admin,
    Owner,
}
