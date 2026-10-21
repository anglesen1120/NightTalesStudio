using ErrorOr;
using NightTales.Application.Abstractions.Identity;

namespace NightTales.Infrastructure.Identity;

/// <summary>
/// The explicit <c>Security:BreachedPasswords:Mode = off</c> check: nothing is compared and nothing is
/// sent anywhere.
/// </summary>
/// <remarks>
/// Reached only because an operator selected <c>off</c>. It is never a fallback: a provider outage in
/// <see cref="BreachedPasswordMode.Range"/> answers 503 <c>service_unavailable</c> and stays there.
/// </remarks>
internal sealed class NoBreachedPasswordChecker : IBreachedPasswordChecker
{
    /// <inheritdoc />
    public Task<ErrorOr<bool>> IsBreachedAsync(string password, CancellationToken ct) =>
        Task.FromResult<ErrorOr<bool>>(false);
}
