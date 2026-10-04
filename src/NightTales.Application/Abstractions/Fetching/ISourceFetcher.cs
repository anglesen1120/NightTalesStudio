using ErrorOr;

namespace NightTales.Application.Abstractions.Fetching;

/// <summary>
/// Fetches one user-supplied source URL and returns its extracted text or a D8 error (02.5.2-T1). A failure is data:
/// the error carries the D8 code (and the HTTP status in <c>httpStatus</c> metadata when the host answered), never an
/// exception; cancellation propagates.
/// </summary>
public interface ISourceFetcher
{
    /// <summary>Fetches <paramref name="request"/>.</summary>
    /// <param name="request">The URL, its registrable domain and the attempt number.</param>
    /// <param name="ct">Cancels the fetch (host shutdown); the 10 s budget is enforced inside.</param>
    /// <returns>The extracted source, or one of the D8 codes.</returns>
    ValueTask<ErrorOr<FetchedSource>> FetchAsync(SourceFetchRequest request, CancellationToken ct);
}
