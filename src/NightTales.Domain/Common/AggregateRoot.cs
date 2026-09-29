namespace NightTales.Domain.Common;

/// <summary>
/// Consistency boundary of the domain (09 README D3). <see cref="Version"/> is the optimistic
/// concurrency token: the persistence model maps it to the PostgreSQL <c>xmin</c> system column
/// (09 README D8, N8) and it is exposed to HTTP clients as an ETag.
/// </summary>
public abstract class AggregateRoot : Entity
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public uint Version { get; private set; }

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}
