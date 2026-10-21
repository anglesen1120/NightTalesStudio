using System.Buffers;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using ErrorOr;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Common.Errors;

namespace NightTales.Infrastructure.Storage;

/// <summary>
/// The one <see cref="IBlobStore"/> of the platform (09 README BD10): AWSSDK.S3 4.x against the
/// S3-compatible SeaweedFS gateway every environment runs (11 D21).
/// </summary>
/// <remarks>
/// <para>
/// The verified 4.0.103.4 surface (step-1 spike, recorded in the task file's Notes):
/// <c>PutObjectAsync</c>, <c>GetObjectAsync</c> (with <c>ByteRange</c>), <c>GetObjectMetadataAsync</c>
/// (a <c>HEAD</c>), <c>DeleteObjectAsync</c>, <c>ListObjectsV2Async</c> and the synchronous
/// <c>GetPreSignedURL</c>. There is <em>no</em> <c>PaginatedListObjectsV2Async</c> on the client in
/// 4.0.103.4, so <see cref="ListKeysAsync"/> pages with <c>ContinuationToken</c> by hand.
/// </para>
/// <para>
/// Every failure — transport, timeout or <c>AccessDenied</c> — is mapped to
/// <c>service_unavailable</c> (503, D17) and never escapes as an exception; the exception's message is
/// logged at <c>Warning</c> with the bucket and the key's first segment only (never the whole key,
/// never a signature, never a credential, BD16).
/// </para>
/// </remarks>
public sealed partial class S3BlobStore : IBlobStore
{
    /// <summary>The fixed, client-safe description of an unreachable or failing store.</summary>
    public const string UnavailableDescription = "The storage service is temporarily unavailable; retry.";

    /// <summary>The fixed, client-safe description of a refused operation.</summary>
    public const string AccessDeniedDescription = "The storage service refused this operation.";

    private const int ListPageSize = 1000;

    /// <summary>
    /// The content type a read URL is signed for. The port's presign member carries no content type, and
    /// its first consumer is the episode video (09.7.1-T1/10.5.2); a later consumer of another type
    /// extends the port rather than mislabelling this one.
    /// </summary>
    private const string PresignedContentType = "video/mp4";

    private readonly IAmazonS3 _client;
    private readonly IAmazonS3 _presignClient;
    private readonly Protocol _presignProtocol;
    private readonly StorageMetrics _metrics;
    private readonly ILogger<S3BlobStore> _logger;

    /// <summary>Builds the store over the host's clients.</summary>
    /// <param name="clients">The factory owning the S3 clients and the presign protocol.</param>
    /// <param name="metrics">The operations counter.</param>
    /// <param name="logger">Receives the failure detail, which never reaches a response body.</param>
    public S3BlobStore(S3ClientFactory clients, StorageMetrics metrics, ILogger<S3BlobStore> logger)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        _client = clients.Client;
        _presignClient = clients.PresignClient;
        _presignProtocol = clients.PresignProtocol;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ErrorOr<BlobInfo>> PutAsync(
        string bucket,
        string key,
        Stream content,
        string contentType,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        try
        {
            if (content.CanSeek)
            {
                // A seekable body is read more than once by the SDK (checksums, payload signing, retries)
                // with its position reset in between, so hashing it locally in one pass is the only way
                // to get a digest that matches exactly the bytes the store accepted.
                var start = content.Position;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var hashed = await HashAsync(content, hash, cancellationToken).ConfigureAwait(false);
                content.Position = start;

                await PutObjectAsync(bucket, key, content, hashed, contentType, cancellationToken).ConfigureAwait(false);

                _metrics.Operation(StorageMetrics.PutOperation, StorageMetrics.OkOutcome);

                return new BlobInfo(hashed, Convert.ToHexStringLower(hash.GetHashAndReset()));
            }

            // A non-seekable body has an unknown length, and AWSSDK.S3 4.x requires a known length to
            // frame a plain (non aws-chunked) PUT: AmazonS3PostMarshallHandler.GetStreamWithLength throws
            // "Could not determine content length" when neither Length nor the Content-Length hint is
            // set, and SetStreamChecksum then forces aws-chunked transfer encoding, which SeaweedFS
            // persists as the object's Content-Encoding (QA-ST-2/QA-ST-4). Spooling the source to a
            // private temp file, hashing in the same pass, turns it into a seekable body of known
            // length without holding the payload in memory; the source stream stays the caller's.
            await using var spool = await SpooledUpload.CreateAsync(content, cancellationToken).ConfigureAwait(false);

            await PutObjectAsync(bucket, key, spool.Stream, spool.Bytes, contentType, cancellationToken).ConfigureAwait(false);

            _metrics.Operation(StorageMetrics.PutOperation, StorageMetrics.OkOutcome);

            return new BlobInfo(spool.Bytes, spool.Sha256);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return MapFailure(exception, StorageMetrics.PutOperation, bucket, key);
        }
    }

    /// <inheritdoc />
    public Task<ErrorOr<BlobObject>> OpenReadAsync(
        string bucket,
        string key,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return ReadAsync(bucket, key, byteRange: null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ErrorOr<BlobObject>> OpenReadAsync(
        string bucket,
        string key,
        long fromByte,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfNegative(fromByte);

        // The open-ended range ("bytes=N-"), not (N, long.MaxValue): S3 documents the first form and the
        // spike confirmed both reach the request as that header.
        return ReadAsync(bucket, key, $"bytes={fromByte}-", cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ErrorOr<Success>> DeleteAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        try
        {
            await _client.DeleteObjectAsync(
                new DeleteObjectRequest { BucketName = bucket, Key = key },
                cancellationToken).ConfigureAwait(false);

            _metrics.Operation(StorageMetrics.DeleteOperation, StorageMetrics.OkOutcome);

            return Result.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AmazonS3Exception exception) when (IsNotFound(exception))
        {
            // Some gateways answer 404 for a delete of a key that is already gone; the port promises that
            // a missing key is success, which 09.7.3-T1's re-runs depend on.
            _metrics.Operation(StorageMetrics.DeleteOperation, StorageMetrics.OkOutcome);

            return Result.Success;
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return MapFailure(exception, StorageMetrics.DeleteOperation, bucket, key);
        }
    }

    /// <inheritdoc />
    public async Task<ErrorOr<bool>> ExistsAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        try
        {
            await _client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = bucket, Key = key },
                cancellationToken).ConfigureAwait(false);

            _metrics.Operation(StorageMetrics.ExistsOperation, StorageMetrics.OkOutcome);

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AmazonS3Exception exception) when (IsNotFound(exception))
        {
            _metrics.Operation(StorageMetrics.ExistsOperation, StorageMetrics.OkOutcome);

            return false;
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return MapFailure(exception, StorageMetrics.ExistsOperation, bucket, key);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ListKeysAsync(
        string bucket,
        string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentNullException.ThrowIfNull(prefix);

        string? continuationToken = null;

        while (true)
        {
            ListObjectsV2Response page;

            try
            {
                page = await _client.ListObjectsV2Async(
                    new ListObjectsV2Request
                    {
                        BucketName = bucket,
                        Prefix = prefix,
                        MaxKeys = ListPageSize,
                        ContinuationToken = continuationToken,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                // An async stream carries no ErrorOr: record the outcome here and let the job that owns
                // the sweep see the provider's own exception (13.3.1-T1).
                RecordFailure(exception, StorageMetrics.ListOperation, bucket, prefix);

                throw;
            }

            foreach (var s3Object in page.S3Objects ?? [])
            {
                if (!string.IsNullOrEmpty(s3Object.Key))
                {
                    yield return s3Object.Key;
                }
            }

            if (page.IsTruncated != true || string.IsNullOrEmpty(page.NextContinuationToken))
            {
                yield break;
            }

            continuationToken = page.NextContinuationToken;
        }
    }

    /// <inheritdoc />
    public ErrorOr<Uri> CreateReadUrl(string bucket, string key, TimeSpan ttl, string? downloadFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        try
        {
            var url = _presignClient.GetPreSignedURL(new GetPreSignedUrlRequest
            {
                BucketName = bucket,
                Key = key,
                Verb = HttpVerb.GET,
                Expires = DateTime.UtcNow.Add(ttl),

                // The scheme follows the endpoint, not a hard-coded HTTPS: the dev emulator is http.
                Protocol = _presignProtocol,
                ResponseHeaderOverrides = new ResponseHeaderOverrides
                {
                    ContentDisposition = SafeContentDisposition.Attachment(downloadFileName),
                    ContentType = PresignedContentType,
                },
            });

            _metrics.Operation(StorageMetrics.PresignOperation, StorageMetrics.OkOutcome);

            return new Uri(url, UriKind.Absolute);
        }
        catch (Exception exception) when (IsStorageFailure(exception) || exception is UriFormatException)
        {
            RecordFailure(exception, StorageMetrics.PresignOperation, bucket, key);

            return Error.Failure(ErrorCodes.ServiceUnavailable, UnavailableDescription);
        }
    }

    private async Task<ErrorOr<BlobObject>> ReadAsync(
        string bucket,
        string key,
        string? byteRange,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client.GetObjectAsync(
                new GetObjectRequest { BucketName = bucket, Key = key, ByteRange = byteRange is null ? null : new ByteRange(byteRange) },
                cancellationToken).ConfigureAwait(false);

            _metrics.Operation(StorageMetrics.ReadOperation, StorageMetrics.OkOutcome);

            // The caller owns the stream (and therefore the response): disposing it releases the
            // connection, so the response is never disposed here.
            return new BlobObject(
                response.ResponseStream,
                response.Headers.ContentType ?? string.Empty,
                FullLength(response));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AmazonS3Exception exception) when (IsNotFound(exception))
        {
            _metrics.Operation(StorageMetrics.ReadOperation, StorageMetrics.OkOutcome);

            return Errors.NotFound();
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return MapFailure(exception, StorageMetrics.ReadOperation, bucket, key);
        }
    }

    private Task<PutObjectResponse> PutObjectAsync(
        string bucket,
        string key,
        Stream body,
        long bytes,
        string contentType,
        CancellationToken cancellationToken) =>
        _client.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                InputStream = body,
                ContentType = contentType,
                Headers = { ContentLength = bytes },
                // Preserve the suffix hashed by PutAsync instead of rewinding to the stream's beginning.
                AutoResetStreamPosition = false,

                // A plain PUT, never aws-chunked. When UseChunkEncoding is left at its default (true)
                // AWSSDK.S3 wraps a signed payload in a chunked stream that sends
                // `Content-Encoding: aws-chunked`, and SeaweedFS 4.48 persists that header as object
                // metadata and echoes it on every presigned GET (QA-ST-4) — a browser can then refuse or
                // mis-decode the video. The body is always seekable here (the caller's, or the spool
                // SpooledUpload produced), so the SDK frames the request with a real Content-Length.
                UseChunkEncoding = false,

                // The caller owns the stream: neither the wrapper nor the SDK may close it.
                AutoCloseStream = false,
            },
            cancellationToken);

    /// <summary>
    /// The object's full size for a read response. A ranged GET reports the <em>slice</em> in
    /// <c>Content-Length</c> and the whole-object total after the slash of <c>Content-Range</c>
    /// (<c>bytes 5-12/13</c>), which is the value the port documents (QA-ST-3).
    /// </summary>
    private static long? FullLength(GetObjectResponse response) =>
        ContentRangeTotal(response.ContentRange) ?? response.ContentLength;

    private static long? ContentRangeTotal(string? contentRange)
    {
        if (string.IsNullOrEmpty(contentRange))
        {
            return null;
        }

        var slash = contentRange.LastIndexOf('/');

        if (slash < 0)
        {
            return null;
        }

        var total = contentRange.AsSpan(slash + 1).Trim();

        return long.TryParse(total, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static async Task<long> HashAsync(Stream content, IncrementalHash hash, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81_920);
        try
        {
            long total = 0;
            while (true)
            {
                var read = await content.ReadAsync(buffer.AsMemory(0, 81_920), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return total;
                }

                hash.AppendData(buffer, 0, read);
                total += read;
            }
        }
        finally
        {
            // Private media must not remain in the shared pool after this upload.
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private Error MapFailure(Exception exception, string operation, string bucket, string key)
    {
        RecordFailure(exception, operation, bucket, key);

        return Error.Failure(
            ErrorCodes.ServiceUnavailable,
            IsAccessDenied(exception) ? AccessDeniedDescription : UnavailableDescription);
    }

    private void RecordFailure(Exception exception, string operation, string bucket, string key)
    {
        var denied = IsAccessDenied(exception);

        _metrics.Operation(operation, denied ? StorageMetrics.DeniedOutcome : StorageMetrics.FailedOutcome);
        LogOperationFailed(exception, operation, bucket, KeyPrefix(key), denied ? "denied" : "unavailable");
    }

    /// <summary>
    /// The SDK and upload-spool failure surface: service/client/transport errors, local I/O or access
    /// errors, and timeouts. A cancellation the caller asked for is never one of them.
    /// </summary>
    private static bool IsStorageFailure(Exception exception) =>
        exception is AmazonS3Exception
            or AmazonClientException
            or HttpRequestException
            or IOException
            or UnauthorizedAccessException
            or TimeoutException
            or OperationCanceledException;

    private static bool IsNotFound(AmazonS3Exception exception) =>
        exception.StatusCode == HttpStatusCode.NotFound
            || string.Equals(exception.ErrorCode, "NoSuchKey", StringComparison.Ordinal);

    private static bool IsAccessDenied(Exception exception) =>
        exception is AmazonS3Exception s3Exception
            && (s3Exception.StatusCode == HttpStatusCode.Forbidden
                || string.Equals(s3Exception.ErrorCode, "AccessDenied", StringComparison.Ordinal));

    /// <summary>
    /// The first segment of a key — the only part a log line may carry, because a full key names a user
    /// and an episode (BD16).
    /// </summary>
    private static string KeyPrefix(string key)
    {
        var separator = key.IndexOf('/', StringComparison.Ordinal);

        return separator <= 0 ? key : key[..separator];
    }

    [LoggerMessage(
        EventId = 2110,
        Level = LogLevel.Warning,
        Message = "storage.operation.failed op={Operation} bucket={Bucket} keyPrefix={KeyPrefix} outcome={Outcome}")]
    private partial void LogOperationFailed(
        Exception exception,
        string operation,
        string bucket,
        string keyPrefix,
        string outcome);
}
