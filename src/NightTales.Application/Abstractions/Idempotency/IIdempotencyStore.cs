namespace NightTales.Application.Abstractions.Idempotency;

/// <summary>
/// The <c>idempotency_keys</c> protocol of a paid command (09 README D6). Every call runs inside the
/// command's transaction, so a claim and its effects are one unit: a rolled-back command removes its
/// claim, and a twin that waited on the unique index replays the committed response.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Claims <paramref name="key"/> for <paramref name="userId"/> with the request hash of
    /// <paramref name="requestHash"/>, inside the caller's transaction.
    /// </summary>
    /// <param name="userId">The authenticated owner of the key; a key is never shared across users.</param>
    /// <param name="key">The client's <c>Idempotency-Key</c>; 1–128 printable ASCII characters.</param>
    /// <param name="requestHash">The canonical SHA-256 hex of the request body.</param>
    /// <param name="now">The clock instant the claim's 24 h lifetime starts from.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    Task<IdempotencyClaim> ClaimAsync(
        Guid userId,
        string key,
        string requestHash,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>Stores the success value of the claimed request, so a later replay returns it.</summary>
    /// <param name="claimId">The <see cref="IdempotencyClaim.Fresh"/> claim this request owns.</param>
    /// <param name="responseType">The CLR full name of the stored success value.</param>
    /// <param name="responseJson">The success value as JSON text.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    Task CompleteAsync(
        Guid claimId,
        string responseType,
        string responseJson,
        CancellationToken cancellationToken);
}

/// <summary>What <see cref="IIdempotencyStore.ClaimAsync"/> decided for one request (09 README D6).</summary>
/// <remarks>
/// The plan (09.1.3-T2 Design) spells the first case "New"; the CLR name is <c>Fresh</c> because
/// <c>New</c> is a reserved member name for the analyzers (CA1716). The protocol is unchanged.
/// </remarks>
public abstract record IdempotencyClaim
{
    /// <summary>The key is this request's: the handler runs and completes the claim.</summary>
    /// <param name="ClaimId">The row the request owns; pass it to <see cref="IIdempotencyStore.CompleteAsync"/>.</param>
    public sealed record Fresh(Guid ClaimId) : IdempotencyClaim;

    /// <summary>The key was already used with the same body: the stored value is returned instead.</summary>
    /// <param name="ResponseType">The CLR full name stored when the first request completed.</param>
    /// <param name="ResponseJson">The stored success value as JSON text.</param>
    public sealed record Replay(string ResponseType, string ResponseJson) : IdempotencyClaim;

    /// <summary>The key was already used with a different body → 422 <c>idempotency_key_reused</c>.</summary>
    public sealed record Mismatch : IdempotencyClaim;
}
