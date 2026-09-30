namespace NightTales.Domain.Common;

/// <summary>
/// Marks a domain event that is allowed to have no user (09 README D11): a platform-wide event raised by
/// an aggregate that is not <see cref="IUserOwned"/>, whose <c>outbox_messages.user_id</c> is therefore
/// <c>null</c>.
/// </summary>
/// <remarks>
/// Without this attribute the outbox interceptor refuses to store an event whose aggregate has no user,
/// because a handler must never run unfiltered by accident (09 plan §8, UC6): the missing user is a bug
/// in the raising command, and the command fails instead of the Worker dead-lettering later.
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class PlatformEventAttribute : Attribute;
