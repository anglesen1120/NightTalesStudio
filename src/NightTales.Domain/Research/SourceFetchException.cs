using ErrorOr;

namespace NightTales.Domain.Research;

/// <summary>
/// Thrown inside the fetcher's transport layers (connect callback, robots, size cap) to unwind to the single catch in
/// <c>SafeHttpSourceFetcher.FetchAsync</c>, which maps it to an <see cref="Error"/>. A fetch failure is data at the
/// port; this exception never leaves the fetcher.
/// </summary>
public sealed class SourceFetchException : Exception
{
    /// <summary>Creates the exception for a catalogued fetch error.</summary>
    /// <param name="error">One of the <see cref="SourceErrors"/> fetch errors.</param>
    /// <param name="httpStatus">The status the host answered with, when it answered.</param>
    public SourceFetchException(Error error, int? httpStatus = null)
        : base(error.Code)
    {
        Error = error;
        HttpStatus = httpStatus;
    }

    /// <summary>The catalogued error.</summary>
    public Error Error { get; }

    /// <summary>The status the host answered with, when it answered.</summary>
    public int? HttpStatus { get; }

    /// <summary>The error as a <see cref="SourceFetchError"/>.</summary>
    public SourceFetchError ToFetchError() => new(Error.Code, HttpStatus);
}
