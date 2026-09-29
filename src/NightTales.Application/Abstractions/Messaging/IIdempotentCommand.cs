namespace NightTales.Application.Abstractions.Messaging;

/// <summary>
/// Marks a command whose success value is stored per <c>(user_id, key)</c> and replayed for a
/// repeated key (09 README D6). The behaviour claims the key inside the UnitOfWork transaction.
/// </summary>
public interface IIdempotentCommand
{
    /// <summary>Opaque 1–128 printable ASCII client key; <c>null</c> when the endpoint did not require one.</summary>
    string? IdempotencyKey { get; }
}
