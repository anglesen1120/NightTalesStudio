namespace NightTales.Application.Abstractions.Storage;

/// <summary>
/// Turns a stored object key of the public bucket into the URL a client may fetch (09.4.2-T1, plan §3).
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately <em>not</em> <c>IBlobStore</c> (BD10, 09.7.2-T1): the public catalog bucket
/// needs no signing, no credential and no round trip, so a catalog query keeps its single dependency
/// and never presigns a public object.
/// </para>
/// <para>
/// The composition is configuration plus a safety check, never trust in the stored value: a
/// <see langword="null"/> or blank key, a base URL that is missing or not usable (see the
/// implementation), or a key that is absolute, root-relative, contains a backslash or a <c>..</c>
/// segment yields <see langword="null"/> — the client then hides the play control instead of following
/// a URL the platform did not compose (plan §8).
/// </para>
/// </remarks>
public interface IPublicAssetUrl
{
    /// <summary>
    /// The public URL of <paramref name="storageKey"/>, or <see langword="null"/> when no safe URL can be
    /// composed.
    /// </summary>
    /// <param name="storageKey">A key of the public bucket, or <see langword="null"/> when the row has none.</param>
    /// <returns>The absolute public URL, or <see langword="null"/>.</returns>
    Uri? Build(string? storageKey);
}
