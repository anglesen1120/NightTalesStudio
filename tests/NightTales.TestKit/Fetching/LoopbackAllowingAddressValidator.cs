using System.Net;
using NightTales.Domain.Research;
using NightTales.Infrastructure.Research.Http;

namespace NightTales.TestKit.Fetching;

/// <summary>
/// The test-only <see cref="IAddressValidator"/> (02 D20): allows loopback (<c>127.0.0.0/8</c> and <c>::1</c>) so a fetch can
/// reach <see cref="HttpTestServer"/>, and delegates every other address to the real
/// <see cref="GlobalUnicastAddressValidator"/>. It is injectable and absent by default: production registers only
/// <see cref="GlobalUnicastAddressValidator"/>, and the architecture test
/// <c>ProductionComposition_DoesNotRegisterLoopbackAllowingAddressValidator</c> keeps it that way.
/// </summary>
/// <param name="inner">The validator for every non-loopback address; the production validator by default.</param>
public sealed class LoopbackAllowingAddressValidator(IAddressValidator? inner = null) : IAddressValidator
{
    private readonly IAddressValidator _inner = inner ?? new GlobalUnicastAddressValidator();

    /// <inheritdoc />
    public bool IsAllowed(IPAddress address, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (IPAddress.IsLoopback(address))
        {
            reason = null;

            return true;
        }

        return _inner.IsAllowed(address, out reason);
    }
}
