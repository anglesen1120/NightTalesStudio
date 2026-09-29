namespace NightTales.Domain.Common;

/// <summary>
/// Base class of every persisted domain type (09 README D3). Ids are UUIDv7 values produced by
/// the domain factories; the model maps <see cref="Id"/> with <c>ValueGeneratedNever()</c>.
/// </summary>
public abstract class Entity
{
    public Guid Id { get; protected init; }
}
