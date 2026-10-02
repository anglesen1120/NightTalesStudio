using NightTales.Application.Abstractions.Storage;

namespace NightTales.Infrastructure.Storage;

/// <summary>
/// The singleton cache between <see cref="StorageHealthProbe"/> and the readiness check
/// (09 README BD12). The prober writes on its own thread while a request thread reads, so both members
/// go through a volatile field rather than a lock: <see cref="StorageHealth"/> is an immutable record
/// and publishing a whole reference is atomic.
/// </summary>
public sealed class StorageHealthCache : IStorageHealthCache
{
    private StorageHealth _current = StorageHealth.NotChecked;

    /// <inheritdoc />
    public StorageHealth Current => Volatile.Read(ref _current);

    /// <inheritdoc />
    public void Publish(StorageHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);

        Volatile.Write(ref _current, health);
    }
}
