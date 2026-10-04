using ErrorOr;

namespace NightTales.Application.Abstractions.Storage;

/// <summary>
/// The object-storage home of the text a fetcher extracted (02.5.2-T2, 02 D10, plan §5.3): an <see cref="IBlobStore"/>
/// wrapper with the research key shape. The Api writes the object <em>before</em> it updates the source row, so a
/// crash in between leaves an orphan object (cleaned by 13.3's deletion sweep) and never a row pointing at nothing.
/// </summary>
public interface IExtractedTextStore
{
    /// <summary>
    /// Writes <paramref name="text"/> as <c>text/plain; charset=utf-8</c> under
    /// <c>research/{userId}/{episodeId}/{sourceId}/{sha256}.txt</c>. The key is content-addressed, so a retry that
    /// yields the same text overwrites the same object and a changed text writes a new one.
    /// </summary>
    /// <param name="userId">The owner.</param>
    /// <param name="episodeId">The episode.</param>
    /// <param name="sourceId">The source.</param>
    /// <param name="sha256">The 64-character lowercase hex SHA-256 of the UTF-8 text.</param>
    /// <param name="text">The validated text, written exactly as given.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <returns>The object key, or <c>service_unavailable</c>.</returns>
    Task<ErrorOr<string>> WriteAsync(
        Guid userId,
        Guid episodeId,
        Guid sourceId,
        string sha256,
        string text,
        CancellationToken cancellationToken);

    /// <summary>Reads the text stored under <paramref name="key"/>.</summary>
    /// <param name="key">A key returned by <see cref="WriteAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The text, <c>not_found</c>, or <c>service_unavailable</c>.</returns>
    Task<ErrorOr<string>> ReadAsync(string key, CancellationToken cancellationToken);
}
