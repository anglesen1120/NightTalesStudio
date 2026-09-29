namespace NightTales.Domain.Common;

/// <summary>
/// Marks a user-owned entity: the <c>"tenant"</c> query filter (09 README D10) is applied to
/// every entity type that implements this interface.
/// </summary>
public interface IUserOwned
{
    Guid UserId { get; }
}
