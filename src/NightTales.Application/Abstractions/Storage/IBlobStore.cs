using ErrorOr;

namespace NightTales.Application.Abstractions.Storage;

/// <summary>
/// What a stored object cost, as the store accepted it (09 README BD10 / WF03): the byte count and the
/// lowercase SHA-256 hex of exactly the bytes written, so the caller can persist both on the
/// <c>assets</c> row without reading the object back.
/// </summary>
/// <param name="Bytes">The number of bytes the store consumed from the source stream.</param>
/// <param name="Sha256">The lowercase hex SHA-256 of those bytes (64 characters).</param>
public sealed record BlobInfo(long Bytes, string Sha256);

/// <summary>
/// An object opened for reading (09 README BD10): the caller owns <paramref name="Content"/> and must
/// dispose it. <paramref name="Length"/> is the object's full size as the store reports it, which for a
/// ranged read is the size of the whole object, not of the returned slice.
/// </summary>
/// <param name="Content">The readable body; the caller disposes it.</param>
/// <param name="ContentType">The content type stored with the object.</param>
/// <param name="Length">The object's full length, when the store reports one.</param>
public sealed record BlobObject(Stream Content, string ContentType, long? Length);

/// <summary>
/// The one blob-storage port of the platform (09 README BD10): every consumer — the render ingest
/// (09.6.3-T2), the research text store (02.5.2-T2), the retention sweep (09.7.3-T1) and the deletion
/// engine (13.3.1-T1) — talks to object storage through it, and the only implementation is the
/// S3-compatible <c>S3BlobStore</c> against the self-hosted SeaweedFS gateway (11 D21).
/// </summary>
/// <remarks>
/// <para>
/// The members that can fail return <see cref="ErrorOr{T}"/> and never an exception: a transport
/// failure or an <c>AccessDenied</c> is <c>service_unavailable</c> (503), the D17 mapping.
/// <see cref="ListKeysAsync"/> is the one exception — an async stream has nowhere to carry the error —
/// so a failing sweep throws the provider's exception to the job that owns the operation, which reports
/// its own outcome (D12).
/// </para>
/// <para>
/// Bucket names are always passed in; the caller resolves them through <see cref="BlobLocator"/>, so
/// the mapping from an <c>assets</c> row to a bucket lives in one place.
/// </para>
/// </remarks>
public interface IBlobStore
{
    /// <summary>
    /// Stores <paramref name="content"/> under <paramref name="key"/> in one streaming pass and
    /// returns what it wrote.
    /// </summary>
    /// <param name="bucket">The bucket, resolved by the caller.</param>
    /// <param name="key">The object key.</param>
    /// <param name="content">The source stream; the store reads it but never disposes it.</param>
    /// <param name="contentType">The content type to store with the object.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <returns>The byte count and SHA-256, or <c>service_unavailable</c>.</returns>
    Task<ErrorOr<BlobInfo>> PutAsync(
        string bucket,
        string key,
        Stream content,
        string contentType,
        CancellationToken cancellationToken);

    /// <summary>Opens the whole object for reading.</summary>
    /// <param name="bucket">The bucket of the object.</param>
    /// <param name="key">The object key.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The body, or <c>not_found</c> when the key does not exist, or <c>service_unavailable</c>.</returns>
    Task<ErrorOr<BlobObject>> OpenReadAsync(string bucket, string key, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the object from <paramref name="fromByte"/> on — the resume half of the chunked YouTube
    /// upload (06.2.3-T2, 06 CR-12).
    /// </summary>
    /// <param name="bucket">The bucket of the object.</param>
    /// <param name="key">The object key.</param>
    /// <param name="fromByte">The first byte of the returned slice, 0-based.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The slice, or <c>not_found</c>/<c>service_unavailable</c>.</returns>
    Task<ErrorOr<BlobObject>> OpenReadAsync(
        string bucket,
        string key,
        long fromByte,
        CancellationToken cancellationToken);

    /// <summary>Removes the object; a key that is already gone is success (09.7.3-T1 re-runs).</summary>
    /// <param name="bucket">The bucket of the object.</param>
    /// <param name="key">The object key.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>Success, or <c>service_unavailable</c>.</returns>
    Task<ErrorOr<Success>> DeleteAsync(string bucket, string key, CancellationToken cancellationToken);

    /// <summary>Answers whether the key exists, with one metadata request and no body read.</summary>
    /// <param name="bucket">The bucket of the object.</param>
    /// <param name="key">The object key.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>True when the key exists, or <c>service_unavailable</c>.</returns>
    Task<ErrorOr<bool>> ExistsAsync(string bucket, string key, CancellationToken cancellationToken);

    /// <summary>
    /// Streams every key under <paramref name="prefix"/> — the prefix sweep of the deletion engine
    /// (13.3.1-T1, 13 CR-6).
    /// </summary>
    /// <param name="bucket">The bucket to sweep.</param>
    /// <param name="prefix">The key prefix.</param>
    /// <param name="cancellationToken">Cancels the sweep.</param>
    /// <returns>The keys, in store order.</returns>
    /// <remarks>
    /// An <see cref="IAsyncEnumerable{T}"/> cannot carry an <see cref="ErrorOr{T}"/>, so a provider
    /// failure surfaces as the provider's own exception to the job that owns the sweep, which reports
    /// its outcome itself (D12).
    /// </remarks>
    IAsyncEnumerable<string> ListKeysAsync(string bucket, string prefix, CancellationToken cancellationToken);

    /// <summary>
    /// Builds a presigned <c>GET</c> URL for a browser (09 README BD10): the signature is computed
    /// locally — no request is made — and the URL is valid for <paramref name="ttl"/>.
    /// </summary>
    /// <param name="bucket">The bucket of the object.</param>
    /// <param name="key">The object key.</param>
    /// <param name="ttl">How long the URL stays valid.</param>
    /// <param name="downloadFileName">The download name, or <see langword="null"/> for no disposition.</param>
    /// <returns>The signed URL, pointing at the browser-reachable endpoint, or <c>service_unavailable</c>.</returns>
    /// <remarks>
    /// Synchronous on purpose (BD10: signing is local, so there is nothing to await), and wrapped in
    /// <see cref="ErrorOr{T}"/> like the other members: a store that cannot sign must answer 503 through
    /// the endpoint's error channel, not with an exception that would leave the 404/503 contract
    /// (09.7.2-T1 acceptance "storage fails → 503").
    /// </remarks>
    ErrorOr<Uri> CreateReadUrl(string bucket, string key, TimeSpan ttl, string? downloadFileName);
}
