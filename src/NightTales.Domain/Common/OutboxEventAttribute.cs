namespace NightTales.Domain.Common;

/// <summary>
/// The stable wire name of a domain event (<c>story_approved.v1</c>), stored in
/// <c>outbox_messages.type</c> (09 README D11). The name is what the Worker dispatcher resolves the CLR
/// type by, so renaming the CLR type is safe while changing this string is a new event version.
/// </summary>
/// <remarks>
/// Every <see cref="IDomainEvent"/> implementation must carry it; the architecture test
/// <c>DomainEvents_HaveOutboxEventAttribute</c> fails the build otherwise. Purely a Domain attribute:
/// the Application and Infrastructure layers only read it through
/// <c>NightTales.Infrastructure.Persistence.Outbox.OutboxEventRegistry</c>.
/// </remarks>
/// <param name="name">The stable name, <c>^[a-z_]+\.v[0-9]+$</c> (09 plan §5).</param>
[AttributeUsage(AttributeTargets.Class)]
public sealed class OutboxEventAttribute(string name) : Attribute
{
    /// <summary>The stable event name the outbox stores and the dispatcher resolves.</summary>
    public string Name { get; } = name;
}
