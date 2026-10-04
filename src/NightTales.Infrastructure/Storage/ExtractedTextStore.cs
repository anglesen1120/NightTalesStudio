using System.Text;
using ErrorOr;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Common.Errors;

namespace NightTales.Infrastructure.Storage;

/// <summary>
/// <see cref="IExtractedTextStore"/> over the platform's one <see cref="IBlobStore"/> (02.5.2-T2, 02 D10, plan §5.3):
/// the research text lives in the <em>media</em> bucket under
/// <c>research/{userId}/{episodeId}/{sourceId}/{sha256}.txt</c>.
/// </summary>
/// <remarks>
/// The key is content-addressed, so a retry that yields the same text overwrites the same object and a changed text
/// writes a new one. Nothing here logs: the key holds ids and a hash only, but the text and the URL never leave the
/// caller (D19).
/// </remarks>
/// <param name="blobs">The blob store.</param>
/// <param name="settings">The storage settings, for the bucket.</param>
public sealed class ExtractedTextStore(IBlobStore blobs, StorageSettings settings) : IExtractedTextStore
{
    /// <summary>The key prefix every object of this store lives under; 13.3's deletion sweep must cover it.</summary>
    public const string KeyPrefix = "research/";

    /// <summary>The content type stored with every object.</summary>
    public const string ContentType = "text/plain; charset=utf-8";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The key of the text of <paramref name="sourceId"/> with hash <paramref name="sha256"/>.</summary>
    /// <param name="userId">The owner.</param>
    /// <param name="episodeId">The episode.</param>
    /// <param name="sourceId">The source.</param>
    /// <param name="sha256">The 64-character lowercase hex SHA-256.</param>
    public static string KeyFor(Guid userId, Guid episodeId, Guid sourceId, string sha256) =>
        $"{KeyPrefix}{userId:D}/{episodeId:D}/{sourceId:D}/{sha256}.txt";

    /// <inheritdoc />
    public async Task<ErrorOr<string>> WriteAsync(
        Guid userId,
        Guid episodeId,
        Guid sourceId,
        string sha256,
        string text,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        ArgumentNullException.ThrowIfNull(text);

        var key = KeyFor(userId, episodeId, sourceId, sha256);
        var bytes = StrictUtf8.GetBytes(text);
        await using var content = new MemoryStream(bytes, writable: false);

        var stored = await blobs
            .PutAsync(settings.Buckets.Media, key, content, ContentType, cancellationToken)
            .ConfigureAwait(false);
        if (stored.IsError)
        {
            return stored.Errors;
        }

        // The store hashes exactly the bytes it consumed: a different digest means the object is not the validated text,
        // so it must not be pointed at by a row.
        if (!string.Equals(stored.Value.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
        {
            return Error.Failure(ErrorCodes.ServiceUnavailable, "The stored text does not match its hash.");
        }

        return key;
    }

    /// <inheritdoc />
    public async Task<ErrorOr<string>> ReadAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var opened = await blobs.OpenReadAsync(settings.Buckets.Media, key, cancellationToken).ConfigureAwait(false);
        if (opened.IsError)
        {
            return opened.Errors;
        }

        await using var content = opened.Value.Content;
        using var reader = new StreamReader(content, StrictUtf8);

        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }
}
