using NightTales.Domain.Common;

namespace NightTales.Infrastructure.UnitTests.TestKit.Probes;

/// <summary>
/// Test-only aggregate: an <see cref="AggregateRoot"/> (so the xmin convention applies), with a
/// <see cref="DateTimeOffset"/> column, an enum column and a JSON column. Never mapped in
/// <c>src</c> — it is added to the model by <see cref="ProbeModelCustomizer"/>.
/// </summary>
internal sealed class ProbeAggregate : AggregateRoot, IUserOwned
{
    private ProbeAggregate()
    {
    }

    public Guid UserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public ProbeStatus Status { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public ProbeSettings? Settings { get; private set; }

    public static ProbeAggregate Create(
        Guid userId,
        DateTimeOffset createdAt,
        ProbeStatus status,
        string name,
        ProbeSettings? settings = null)
        => new()
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            CreatedAt = createdAt,
            Status = status,
            Name = name,
            Settings = settings,
        };

    public void Rename(string name) => Name = name;

    /// <summary>
    /// Renames the probe <em>and</em> raises <see cref="ProbeDomainEvent"/> (09.2.4-T1): what the outbox
    /// interceptor turns into an <c>outbox_messages</c> row in the same <c>SaveChanges</c>.
    /// </summary>
    /// <param name="name">The new name.</param>
    /// <param name="occurredAt">When the event happened — a fixed instant, never the wall clock.</param>
    public void RenameWithEvent(string name, DateTimeOffset occurredAt)
    {
        Name = name;
        Raise(new ProbeDomainEvent(Id, name, occurredAt));
    }
}
