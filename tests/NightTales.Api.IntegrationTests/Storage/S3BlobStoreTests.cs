using System.Net;
using System.Security.Cryptography;
using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Common.Errors;
using NightTales.Infrastructure.Storage;
using NightTales.TestKit.Storage;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Storage;

/// <summary>
/// <c>S3BlobStore</c> against the real S3-compatible gateway (09.7.2-T1 step 1 and its Tests table):
/// the round trip, the 404-tolerant helpers, the presign and the client configuration.
/// </summary>
/// <remarks>
/// <para>
/// These tests need the dev SeaweedFS container of 11 D14 (11.1.1-T2's <c>storage</c> service), which no
/// CI job starts yet, so they skip with a named reason when <c>NTS_STORAGE_URL</c> is unset and run in
/// full when it is:
/// <c>NTS_STORAGE_URL=http://localhost:8333 NTS_STORAGE_ACCESS_KEY=… NTS_STORAGE_SECRET_KEY=… dotnet test
/// --filter "Category=Storage"</c>. A skip is loud in the test log rather than silently green.
/// </para>
/// <para>
/// The category is the spec's <c>[Trait("Category","Storage")]</c>: it is deliberately not
/// <c>Integration</c>, because these tests need no PostgreSQL — the CI integration job must not pick
/// them up and then fail on a container it does not run.
/// </para>
/// </remarks>
[Trait("Category", "Storage")]
public sealed class S3BlobStoreTests
{
    private const string MarkerBucket = "nt-media-dev";

    [Fact]
    public async Task PutAsync_ReturnsByteCountAndSha256()
    {
        var store = await StoreAsync();
        var key = KeyFor("put");
        var content = Payload("nighttales-put");
        var ct = TestContext.Current.CancellationToken;

        await using var source = new MemoryStream(content);

        var info = await store.PutAsync(MarkerBucket, key, source, "video/mp4", TestContext.Current.CancellationToken);

        info.IsError.ShouldBeFalse(info.FirstError.ToString());

        // sha256sum over the same bytes is the reference: the digest must be the lowercase hex of them.
        var expected = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        info.Value.Bytes.ShouldBe(content.LongLength);
        info.Value.Sha256.ShouldBe(expected);

        // QA-ST-4: a signed PUT must not leave aws-chunked on the object; SeaweedFS persists that
        // Content-Encoding and a browser then refuses or mis-decodes the video.
        await AssertPlainEncodingAsync(key, ct);

        await store.DeleteAsync(MarkerBucket, key, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OpenReadAsync_RoundTripsContentAndType()
    {
        var store = await StoreAsync();
        var key = KeyFor("roundtrip");
        var content = Payload("nighttales-roundtrip");
        var ct = TestContext.Current.CancellationToken;

        await using var source = new MemoryStream(content);
        (await store.PutAsync(MarkerBucket, key, source, "video/mp4", ct)).IsError.ShouldBeFalse();

        var read = await store.OpenReadAsync(MarkerBucket, key, ct);

        read.IsError.ShouldBeFalse(read.FirstError.ToString());
        read.Value.ContentType.ShouldBe("video/mp4");
        read.Value.Length.ShouldBe(content.LongLength);

        await using var body = read.Value.Content;
        using var buffer = new MemoryStream();
        await body.CopyToAsync(buffer, ct);

        buffer.ToArray().ShouldBe(content);

        await store.DeleteAsync(MarkerBucket, key, ct);
    }

    [Fact]
    public async Task ExistsAsync_FalseForMissingKey()
    {
        var store = await StoreAsync();
        var ct = TestContext.Current.CancellationToken;

        var exists = await store.ExistsAsync(MarkerBucket, KeyFor("missing"), ct);

        exists.IsError.ShouldBeFalse(exists.FirstError.ToString());
        exists.Value.ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteAsync_IsIdempotentWhenKeyMissing()
    {
        var store = await StoreAsync();
        var ct = TestContext.Current.CancellationToken;

        var first = await store.DeleteAsync(MarkerBucket, KeyFor("never-written"), ct);
        var second = await store.DeleteAsync(MarkerBucket, KeyFor("never-written"), ct);

        first.IsError.ShouldBeFalse(first.FirstError.ToString());
        second.IsError.ShouldBeFalse(second.FirstError.ToString());
    }

    [Fact]
    public async Task CreateReadUrl_ContainsSignatureAndExpiresAtTtl()
    {
        var store = await StoreAsync();
        var ct = TestContext.Current.CancellationToken;
        var key = KeyFor("presign");
        var content = Payload("nighttales-presign");

        await using var source = new MemoryStream(content);
        (await store.PutAsync(MarkerBucket, key, source, "video/mp4", ct)).IsError.ShouldBeFalse();

        var url = store.CreateReadUrl(MarkerBucket, key, TimeSpan.FromMinutes(15), "tap-mot.mp4");

        url.IsError.ShouldBeFalse(url.FirstError.ToString());
        url.Value.Query.ShouldContain("X-Amz-Signature");
        url.Value.Query.ShouldContain("X-Amz-Expires=900");
        url.Value.AbsolutePath.ShouldContain($"/{MarkerBucket}/", Case.Sensitive, "the gateway is path-style");
        url.Value.Host.ShouldBe(new Uri(Endpoint()).Host);

        // The acceptance leg "the URL works": a real GET through the presigned URL, no credentials.
        using var client = new HttpClient();
        using var response = await client.GetAsync(url.Value, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("video/mp4");
        response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        (await response.Content.ReadAsByteArrayAsync(ct)).ShouldBe(content);

        // QA-ST-4 on the wire: the presigned GET must not advertise aws-chunked (SeaweedFS echoes the
        // stored Content-Encoding), or a browser can refuse the video.
        string.Join(",", response.Content.Headers.ContentEncoding)
            .ShouldNotContain("aws-chunked", Case.Insensitive);

        await store.DeleteAsync(MarkerBucket, key, ct);
    }

    [Fact]
    public async Task CreateReadUrl_HeaderOverrides_AreCarriedInTheSignature()
    {
        var store = await StoreAsync();
        var ct = TestContext.Current.CancellationToken;
        var key = KeyFor("presign-headers");

        await using var source = new MemoryStream(Payload("nighttales-headers"));
        (await store.PutAsync(MarkerBucket, key, source, "video/mp4", ct)).IsError.ShouldBeFalse();

        // The name the endpoint passes: the episode slug plus ".mp4" (VideoSlug.From, 09.7.2-T1). The
        // ASCII parameter is that name sanitised — a space would survive the sanitizer, so the hyphen the
        // slug carries is what the header shows.
        var url = store.CreateReadUrl(MarkerBucket, key, TimeSpan.FromMinutes(15), "tap-mot.mp4");

        url.IsError.ShouldBeFalse(url.FirstError.ToString());
        url.Value.Query.ShouldContain("response-content-type=video%2Fmp4", Case.Sensitive);
        url.Value.Query.ShouldContain("response-content-disposition=", Case.Sensitive);

        using var client = new HttpClient();
        using var response = await client.GetAsync(url.Value, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var disposition = response.Content.Headers.ContentDisposition!.ToString();

        disposition.ShouldStartWith("attachment;");
        disposition.ShouldContain("filename=\"tap-mot.mp4\"", Case.Sensitive, "the ASCII parameter is the slug, folded to ASCII");
        disposition.ShouldContain("filename*=UTF-8''", Case.Sensitive);

        await store.DeleteAsync(MarkerBucket, key, ct);
    }

    [Fact]
    public void S3BlobStore_BuildsClientWithPathStyle_WhenConfigured()
    {
        var settings = new StorageSettings
        {
            ServiceUrl = Endpoint(),
            AccessKey = AccessKey(),
            SecretKey = SecretKey(),
            Region = "us-east-1",
            ForcePathStyle = true,
        };

        using var clients = new S3ClientFactory(Options.Create(settings));

        // The v4 trap the factory exists for: ServiceURL and RegionEndpoint are mutually exclusive, so the
        // endpoint must have survived and the region must travel as AuthenticationRegion.
        var config = (Amazon.S3.AmazonS3Config)clients.Client.Config;

        config.ForcePathStyle.ShouldBeTrue();
        config.ServiceURL.ShouldBe(settings.ServiceUrl + "/");
        config.AuthenticationRegion.ShouldBe("us-east-1");
        clients.PresignProtocol.ShouldBe(
            settings.PresignServiceUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? Amazon.S3.Protocol.HTTPS
                : Amazon.S3.Protocol.HTTP);
    }

    [Fact]
    public async Task PutAsync_NonSeekableStream_ReturnsByteCountAndSha256()
    {
        var store = await StoreAsync();
        var key = KeyFor("nonseekable");
        var content = Payload("nighttales-nonseekable");
        var ct = TestContext.Current.CancellationToken;

        // The render ingest hands the store a stream of unknown length (09.6.3-T2): the digest must
        // still equal the bytes the store accepted, not a hash of a re-read the SDK needed internally.
        await using var source = new NonSeekableStream(content);

        var info = await store.PutAsync(MarkerBucket, key, source, "video/mp4", ct);

        info.IsError.ShouldBeFalse(info.FirstError.ToString());
        info.Value.Bytes.ShouldBe(content.LongLength);
        info.Value.Sha256.ShouldBe(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());

        var read = await store.OpenReadAsync(MarkerBucket, key, ct);
        read.IsError.ShouldBeFalse(read.FirstError.ToString());
        await using var body = read.Value.Content;
        using var roundTripped = new MemoryStream();
        await body.CopyToAsync(roundTripped, ct);
        roundTripped.ToArray().ShouldBe(content);

        // QA-ST-4: spooling makes the body seekable, so the upload is a plain PUT, not aws-chunked.
        await AssertPlainEncodingAsync(key, ct);

        // QA-ST-2: the caller's stream is never disposed by the store (or by the spool).
        (await source.ReadAsync(new byte[1], ct)).ShouldBe(0);

        await store.DeleteAsync(MarkerBucket, key, ct);
    }

    [Fact]
    public async Task PutAsync_ZeroByteStream_RoundTripsAsEmptyObject()
    {
        var store = await StoreAsync();
        var key = KeyFor("zero-byte");
        var ct = TestContext.Current.CancellationToken;

        // The readiness marker is exactly this call: a 0-byte text/plain body. A store that refused
        // or mis-hashed it would keep the prober unhealthy.
        var info = await store.PutAsync(MarkerBucket, key, Stream.Null, "text/plain", ct);

        info.IsError.ShouldBeFalse(info.FirstError.ToString());
        info.Value.Bytes.ShouldBe(0);
        info.Value.Sha256.ShouldBe(Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant());

        var exists = await store.ExistsAsync(MarkerBucket, key, ct);
        exists.IsError.ShouldBeFalse(exists.FirstError.ToString());
        exists.Value.ShouldBeTrue("a 0-byte object is a real object");

        var read = await store.OpenReadAsync(MarkerBucket, key, ct);
        read.IsError.ShouldBeFalse(read.FirstError.ToString());
        read.Value.Length.ShouldBe(0);
        await using var body = read.Value.Content;
        (await body.ReadAsync(new byte[1], ct)).ShouldBe(0);

        await store.DeleteAsync(MarkerBucket, key, ct);
    }

    [Fact]
    public async Task OpenReadAsync_OfMissingKey_ReturnsNotFound()
    {
        var store = await StoreAsync();

        var read = await store.OpenReadAsync(
            MarkerBucket, KeyFor("never-uploaded"), TestContext.Current.CancellationToken);

        read.IsError.ShouldBeTrue();
        read.FirstError.Type.ShouldBe(ErrorOr.ErrorType.NotFound,
            "a missing object is not_found, not service_unavailable");
    }

    [Fact]
    public async Task OpenReadAsync_FromByte_ReturnsOnlyTheTail()
    {
        var store = await StoreAsync();
        var key = KeyFor("ranged");
        var content = Payload("nighttales-ranged");
        var ct = TestContext.Current.CancellationToken;

        await using var source = new MemoryStream(content);
        (await store.PutAsync(MarkerBucket, key, source, "video/mp4", ct)).IsError.ShouldBeFalse();

        const int from = 7;
        var read = await store.OpenReadAsync(MarkerBucket, key, from, ct);

        read.IsError.ShouldBeFalse(read.FirstError.ToString());
        await using var body = read.Value.Content;
        using var tail = new MemoryStream();
        await body.CopyToAsync(tail, ct);

        tail.ToArray().ShouldBe(content[from..], "the resume read returns the bytes after fromByte");
        tail.Length.ShouldBe(content.LongLength - from);

        // QA-ST-3: BlobObject.Length is the whole object's size (Content-Range total), not the slice
        // length Content-Length reports for a ranged GET.
        read.Value.Length.ShouldBe(content.LongLength);

        await store.DeleteAsync(MarkerBucket, key, ct);
    }

    [Fact]
    public async Task UnreachableStore_MapsToServiceUnavailable_NotAnException()
    {
        // A refused connection is the smallest storage outage: every member must surface it through
        // the port's error channel (service_unavailable → 503), never as an exception (BD16, D17).
        var settings = new StorageSettings
        {
            ServiceUrl = StorageTestEndpoint.Unreachable,
            AccessKey = "qa-access",
            SecretKey = "qa-secret",
            Region = "us-east-1",
            ForcePathStyle = true,
        };

        var store = new S3BlobStore(
            new S3ClientFactory(Options.Create(settings)),
            new StorageMetrics(),
            NullLogger<S3BlobStore>.Instance);
        var ct = TestContext.Current.CancellationToken;

        var exists = await store.ExistsAsync(MarkerBucket, KeyFor("unreachable"), ct);
        exists.IsError.ShouldBeTrue();
        exists.FirstError.Code.ShouldBe(ErrorCodes.ServiceUnavailable);

        var read = await store.OpenReadAsync(MarkerBucket, KeyFor("unreachable"), ct);
        read.IsError.ShouldBeTrue();
        read.FirstError.Code.ShouldBe(ErrorCodes.ServiceUnavailable);

        var put = await store.PutAsync(
            MarkerBucket, KeyFor("unreachable"), new MemoryStream([1]), "video/mp4", ct);
        put.IsError.ShouldBeTrue();
        put.FirstError.Code.ShouldBe(ErrorCodes.ServiceUnavailable);
    }

    [Fact]
    public async Task OpenReadAsync_Cancelled_ThrowsInsteadOfReporting503()
    {
        var store = await StoreAsync();
        var key = KeyFor("cancelled");
        var ct = TestContext.Current.CancellationToken;

        await using var source = new MemoryStream(Payload("nighttales-cancelled"));
        (await store.PutAsync(MarkerBucket, key, source, "video/mp4", ct)).IsError.ShouldBeFalse();

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        // A caller-cancelled operation is not a storage fault: the port rethrows the cancellation
        // rather than reporting service_unavailable, so an aborted request can never look like an
        // outage to the caller or to a metric.
        await Should.ThrowAsync<OperationCanceledException>(
            () => store.OpenReadAsync(MarkerBucket, key, cancelled.Token));
        await Should.ThrowAsync<OperationCanceledException>(
            () => store.PutAsync(MarkerBucket, key, new MemoryStream([1]), "video/mp4", cancelled.Token));

        await store.DeleteAsync(MarkerBucket, key, ct);
    }

    [Fact]
    public async Task OpenReadAsync_DisposedMidRead_ReleasesTheConnection()
    {
        var store = await StoreAsync();
        var key = KeyFor("midread");
        var content = new byte[512 * 1024];
        new Random(9_072).NextBytes(content);
        var ct = TestContext.Current.CancellationToken;

        await using var source = new MemoryStream(content);
        (await store.PutAsync(MarkerBucket, key, source, "video/mp4", ct)).IsError.ShouldBeFalse();

        var read = await store.OpenReadAsync(MarkerBucket, key, ct);
        read.IsError.ShouldBeFalse(read.FirstError.ToString());

        // A client that closes a player mid-stream abandons the body: disposing the returned stream
        // is the only teardown the port promises, and it must not fault or wedge the store.

        var buffer = new byte[4096];
        (await read.Value.Content.ReadAsync(buffer, ct)).ShouldBeGreaterThan(0);
        await read.Value.Content.DisposeAsync();

        var exists = await store.ExistsAsync(MarkerBucket, key, ct);
        exists.IsError.ShouldBeFalse(exists.FirstError.ToString());
        exists.Value.ShouldBeTrue();

        await store.DeleteAsync(MarkerBucket, key, ct);
    }

    [Fact]
    public async Task CreateReadUrl_SignsForThePublicEndpoint_WhenConfigured()
    {
        // 10 CR-21: the signature covers the host, so with a distinct browser endpoint the store must
        // sign for that endpoint, not the internal one — a URL signed for ServiceUrl would 403.
        var endpoint = new Uri(Endpoint());
        var settings = new StorageSettings
        {
            ServiceUrl = $"http://{endpoint.Host}:{endpoint.Port}",
            PublicServiceUrl = Endpoint(),
            AccessKey = AccessKey(),
            SecretKey = SecretKey(),
            Region = "us-east-1",
            ForcePathStyle = true,
        };

        var store = new S3BlobStore(
            new S3ClientFactory(Options.Create(settings)),
            new StorageMetrics(),
            NullLogger<S3BlobStore>.Instance);

        var key = KeyFor("public-endpoint");
        var ct = TestContext.Current.CancellationToken;
        await using var source = new MemoryStream(Payload("nighttales-public"));
        (await store.PutAsync(MarkerBucket, key, source, "video/mp4", ct)).IsError.ShouldBeFalse();

        var url = store.CreateReadUrl(MarkerBucket, key, TimeSpan.FromMinutes(2), null);

        url.IsError.ShouldBeFalse(url.FirstError.ToString());
        url.Value.Host.ShouldBe(endpoint.Host, "signed for PublicServiceUrl, not ServiceUrl");
        url.Value.Query.ShouldContain("X-Amz-Expires=120", Case.Sensitive,
            "the ttl is the caller's argument — the 15-minute ceiling lives in the endpoint, not the port");

        using var client = new HttpClient();
        using var response = await client.GetAsync(url.Value, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            "a URL signed for the public host must really answer there");

        await store.DeleteAsync(MarkerBucket, key, ct);
    }

    [Fact]
    public async Task CreateReadUrl_DownloadName_NeverCarriesPathOrControlBytes()
    {
        var store = await StoreAsync();
        var key = KeyFor("hostile-name");
        var ct = TestContext.Current.CancellationToken;

        await using var source = new MemoryStream(Payload("nighttales-hostile"));
        (await store.PutAsync(MarkerBucket, key, source, "video/mp4", ct)).IsError.ShouldBeFalse();

        // Path segments, quotes, backslashes and a CR/LF pair all reach the header builder from an
        // episode title: none may survive into the signed response-content-disposition value.
        var url = store.CreateReadUrl(
            MarkerBucket, key, TimeSpan.FromMinutes(15), "../../\"evil\\name\"\r\nSet-Cookie: x.mp4");

        url.IsError.ShouldBeFalse(url.FirstError.ToString());
        var decoded = Uri.UnescapeDataString(url.Value.Query);
        decoded.ShouldNotContain("\r", Case.Sensitive);
        decoded.ShouldNotContain("\n", Case.Sensitive);

        // The header's own quotes delimit filename="…" and are legal; only the name inside them must
        // be clean.
        var filename = decoded
            .Split("filename=\"", StringSplitOptions.None)[1]
            .Split('\"')[0];
        filename.ShouldNotContain("\"", Case.Sensitive);
        filename.ShouldNotContain("\\", Case.Sensitive);
        filename.ShouldNotContain("../", Case.Sensitive);
        filename.All(char.IsAscii).ShouldBeTrue();

        using var client = new HttpClient();
        using var response = await client.GetAsync(url.Value, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var disposition = response.Content.Headers.ContentDisposition!.ToString();
        disposition.ShouldNotContain("\r", Case.Sensitive);
        disposition.ShouldNotContain("\n", Case.Sensitive);
        disposition.ShouldNotContain("../", Case.Sensitive,
            "the filename the browser saves must not be able to traverse a directory");

        await store.DeleteAsync(MarkerBucket, key, ct);
    }

    [Fact]
    public async Task CreateReadUrl_TamperedSignatureInput_IsRejected()
    {
        var store = await StoreAsync();
        var key = KeyFor("tampered");
        var ct = TestContext.Current.CancellationToken;

        await using var source = new MemoryStream(Payload("nighttales-tampered"));
        (await store.PutAsync(MarkerBucket, key, source, "video/mp4", ct)).IsError.ShouldBeFalse();

        var url = store.CreateReadUrl(MarkerBucket, key, TimeSpan.FromMinutes(15), "film.mp4");
        url.IsError.ShouldBeFalse(url.FirstError.ToString());

        // The header overrides are part of the signature: a proxy or user that rewrites them must get
        // 403, or the safe Content-Disposition could be swapped for an inline one.
        var tampered = new Uri(url.Value.ToString().Replace(
            "response-content-disposition=", "response-content-disposition=inline%3B%20", StringComparison.Ordinal));

        using var client = new HttpClient();
        using var tamperedResponse = await client.GetAsync(tampered, ct);
        tamperedResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // And a signed GET does not serve any other verb (BD10: the presign is a read, not a request
        // ticket).
        using var head = new HttpRequestMessage(HttpMethod.Head, url.Value);
        using var headResponse = await client.SendAsync(head, ct);
        headResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await store.DeleteAsync(MarkerBucket, key, ct);
    }

    /// <summary>A read-only stream that cannot seek, like the render pipeline's upload body.</summary>
    private sealed class NonSeekableStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// QA-ST-4: a raw <c>HEAD</c> through the SDK must not show <c>aws-chunked</c> on the stored object
    /// (SeaweedFS persists the upload's <c>Content-Encoding</c> and echoes it on every GET).
    /// </summary>
    private static async Task AssertPlainEncodingAsync(string key, CancellationToken ct)
    {
        var settings = new StorageSettings
        {
            ServiceUrl = Endpoint(),
            AccessKey = AccessKey(),
            SecretKey = SecretKey(),
            Region = "us-east-1",
            ForcePathStyle = true,
        };

        using var client = S3ClientFactory.Create(Endpoint(), settings);

        var metadata = await client.GetObjectMetadataAsync(
            new Amazon.S3.Model.GetObjectMetadataRequest { BucketName = MarkerBucket, Key = key },
            ct);

        (metadata.ContentEncoding ?? string.Empty)
            .ShouldNotContain("aws-chunked", Case.Insensitive, "a plain PUT must not persist aws-chunked (QA-ST-4)");
    }

    /// <summary>The store under test, or a skip naming the container it needs.</summary>
    private static Task<S3BlobStore> StoreAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(StorageUrlVariable)))
        {
            Assert.Skip(
                $"Set {StorageUrlVariable} to the dev SeaweedFS gateway (11 D14 / 11.1.1-T2) to run the "
                + "storage tests, for example NTS_STORAGE_URL=http://localhost:8333.");
        }

        var settings = new StorageSettings
        {
            ServiceUrl = Endpoint(),
            AccessKey = AccessKey(),
            SecretKey = SecretKey(),
            Region = "us-east-1",
            ForcePathStyle = true,
        };

        var clients = new S3ClientFactory(Options.Create(settings));

        return Task.FromResult(new S3BlobStore(clients, new StorageMetrics(), NullLogger<S3BlobStore>.Instance));
    }

    private const string StorageUrlVariable = "NTS_STORAGE_URL";

    private static string Endpoint() =>
        Environment.GetEnvironmentVariable(StorageUrlVariable) ?? "http://localhost:8333";

    private static string AccessKey() =>
        Environment.GetEnvironmentVariable("NTS_STORAGE_ACCESS_KEY") ?? "nts-dev-access";

    private static string SecretKey() =>
        Environment.GetEnvironmentVariable("NTS_STORAGE_SECRET_KEY") ?? "nts-dev-secret";

    private static string KeyFor(string name) => $"media/test/{name}/{Guid.CreateVersion7():N}.mp4";

    private static byte[] Payload(string seed) => System.Text.Encoding.UTF8.GetBytes($"{seed}-{Guid.CreateVersion7():N}");
}
