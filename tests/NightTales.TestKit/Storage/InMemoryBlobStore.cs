using ErrorOr;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Common.Errors;

namespace NightTales.TestKit.Storage;

/// <summary>The port member a recorded call went through.</summary>
public enum BlobOperation
{
    /// <summary><see cref="IBlobStore.PutAsync"/>.</summary>
    Put,

    /// <summary><see cref="IBlobStore.OpenReadAsync(string, string, CancellationToken)"/>.</summary>
    Read,

    /// <summary><see cref="IBlobStore.DeleteAsync"/>.</summary>
    Delete,

    /// <summary><see cref="IBlobStore.ExistsAsync"/>.</summary>
    Exists,

    /// <summary><see cref="IBlobStore.ListKeysAsync"/>.</summary>
    List,

    /// <summary><see cref="IBlobStore.CreateReadUrl"/>.</summary>
    ReadUrl,
}

/// <summary>One recorded call into <see cref="InMemoryBlobStore"/>.</summary>
/// <param name="Operation">The member that was called.</param>
/// <param name="Bucket">The bucket argument.</param>
/// <param name="Key">The key (or prefix) argument.</param>
public sealed record BlobCall(BlobOperation Operation, string Bucket, string Key);

/// <summary>What the caller asked <see cref="IBlobStore.CreateReadUrl"/> for.</summary>
/// <param name="Bucket">The bucket argument.</param>
/// <param name="Key">The key argument.</param>
/// <param name="Ttl">The requested lifetime.</param>
/// <param name="DownloadFileName">The requested download name, or <see langword="null"/> for no disposition.</param>
public sealed record BlobReadUrlRequest(string Bucket, string Key, TimeSpan Ttl, string? DownloadFileName);

/// <summary>
/// A hand-written, dictionary-backed <see cref="IBlobStore"/> (09 README D13: no mocking library) for
/// every test that must not need the dev storage container.
/// </summary>
/// <remarks>
/// <para>
/// It is a real in-memory store: seeded objects round-trip, a delete is idempotent and keys are listed
/// by prefix. On top of that it <em>records</em> every call (so a test can assert an endpoint touched
/// the store, or that readiness did not) and can be told to fail — either an async member with an
/// <see cref="ErrorOr{T}"/> error, or the presign member with an <see cref="Error"/> — because the port
/// has to answer 503 through the same channel the real store uses.
/// </para>
/// <para>
/// It deliberately does not build a disposition header: a test that needs the header asks
/// <c>SafeContentDisposition</c> for it, exactly as the real store does, so the assertion is about the
/// name the endpoint chose rather than about a fake's own copy of the rule.
/// </para>
/// </remarks>
public sealed class InMemoryBlobStore : IBlobStore
{
    private readonly Dictionary<string, StoredObject> _objects = new(StringComparer.Ordinal);
    private readonly List<BlobCall> _calls = [];
    private readonly List<BlobReadUrlRequest> _readUrlRequests = [];

    /// <summary>Every call, in order.</summary>
    public IReadOnlyList<BlobCall> Calls => _calls;

    /// <summary>Every <see cref="CreateReadUrl"/> request, in order.</summary>
    public IReadOnlyList<BlobReadUrlRequest> ReadUrlRequests => _readUrlRequests;

    /// <summary>
    /// The error every async member answers with, or <see langword="null"/> for a working store. Set it
    /// to <c>service_unavailable</c> to model an unreachable gateway.
    /// </summary>
    public Error? AsyncFailure { get; set; }

    /// <summary>The error <see cref="CreateReadUrl"/> answers with, or <see langword="null"/> to sign.</summary>
    public Error? ReadUrlFailure { get; set; }

    /// <summary>How many calls of one operation were recorded.</summary>
    /// <param name="operation">The operation to count.</param>
    /// <returns>The number of recorded calls.</returns>
    public int CountOf(BlobOperation operation) => _calls.Count(call => call.Operation == operation);

    /// <summary>Places an object in the store without recording a call (test setup, not production traffic).</summary>
    /// <param name="bucket">The bucket.</param>
    /// <param name="key">The key.</param>
    /// <param name="content">The bytes.</param>
    /// <param name="contentType">The stored content type.</param>
    public void Seed(string bucket, string key, byte[] content, string contentType = "video/mp4") =>
        _objects[Locator(bucket, key)] = new StoredObject(content, contentType);

    /// <summary>Whether the key is present.</summary>
    /// <param name="bucket">The bucket.</param>
    /// <param name="key">The key.</param>
    /// <returns>True when the key exists.</returns>
    public bool Contains(string bucket, string key) => _objects.ContainsKey(Locator(bucket, key));

    /// <inheritdoc />
    public async Task<ErrorOr<BlobInfo>> PutAsync(
        string bucket,
        string key,
        Stream content,
        string contentType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        _calls.Add(new BlobCall(BlobOperation.Put, bucket, key));

        if (AsyncFailure is { } failure)
        {
            return failure;
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        var bytes = buffer.ToArray();
        _objects[Locator(bucket, key)] = new StoredObject(bytes, contentType);

        return new BlobInfo(bytes.LongLength, Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
    }

    /// <inheritdoc />
    public Task<ErrorOr<BlobObject>> OpenReadAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        _calls.Add(new BlobCall(BlobOperation.Read, bucket, key));

        return Task.FromResult(Open(bucket, key, fromByte: 0));
    }

    /// <inheritdoc />
    public Task<ErrorOr<BlobObject>> OpenReadAsync(
        string bucket,
        string key,
        long fromByte,
        CancellationToken cancellationToken)
    {
        _calls.Add(new BlobCall(BlobOperation.Read, bucket, key));

        return Task.FromResult(Open(bucket, key, fromByte));
    }

    /// <inheritdoc />
    public Task<ErrorOr<Success>> DeleteAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        _calls.Add(new BlobCall(BlobOperation.Delete, bucket, key));

        if (AsyncFailure is { } failure)
        {
            return Task.FromResult<ErrorOr<Success>>(failure);
        }

        // A missing key is success, exactly as the port promises.
        _objects.Remove(Locator(bucket, key));

        return Task.FromResult<ErrorOr<Success>>(Result.Success);
    }

    /// <inheritdoc />
    public Task<ErrorOr<bool>> ExistsAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        _calls.Add(new BlobCall(BlobOperation.Exists, bucket, key));

        return Task.FromResult(AsyncFailure is { } failure
            ? (ErrorOr<bool>)failure
            : _objects.ContainsKey(Locator(bucket, key)));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ListKeysAsync(
        string bucket,
        string prefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _calls.Add(new BlobCall(BlobOperation.List, bucket, prefix));

        var prefixKey = $"{bucket}\u001f{prefix}";

        foreach (var stored in _objects.Keys.Where(key => key.StartsWith(prefixKey, StringComparison.Ordinal)).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();

            yield return stored[(stored.IndexOf('\u001f') + 1)..];

            await Task.Yield();
        }
    }

    /// <inheritdoc />
    public ErrorOr<Uri> CreateReadUrl(string bucket, string key, TimeSpan ttl, string? downloadFileName)
    {
        _calls.Add(new BlobCall(BlobOperation.ReadUrl, bucket, key));
        _readUrlRequests.Add(new BlobReadUrlRequest(bucket, key, ttl, downloadFileName));

        if (ReadUrlFailure is { } failure)
        {
            return failure;
        }

        // A URL that looks like a real presign (a signature, an expiry and the key) but points nowhere:
        // the log/audit assertions look for exactly these markers.
        return new Uri(
            $"https://storage.test/{bucket}/{key}?X-Amz-Algorithm=AWS4-HMAC-SHA256"
            + $"&X-Amz-Expires={(int)ttl.TotalSeconds}&X-Amz-Signature=0123456789abcdef");
    }

    private static string Locator(string bucket, string key) => $"{bucket}\u001f{key}";

    private ErrorOr<BlobObject> Open(string bucket, string key, long fromByte)
    {
        if (AsyncFailure is { } failure)
        {
            return failure;
        }

        if (!_objects.TryGetValue(Locator(bucket, key), out var stored))
        {
            return Errors.NotFound();
        }

        var offset = checked((int)fromByte);
        var slice = stored.Content[offset..];

        return new BlobObject(new MemoryStream(slice, writable: false), stored.ContentType, stored.Content.LongLength);
    }

    private sealed record StoredObject(byte[] Content, string ContentType);
}
