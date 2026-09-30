using NightTales.Application.Abstractions.Security;

namespace NightTales.Application.Abstractions.Identity;

/// <summary>
/// The ambient request metadata of the current scope (09.3.1-T1). A command handler that must record
/// where a request came from reads it here; the HTTP host implements it over its
/// <c>HttpContext</c>, so the Application layer stays free of ASP.NET Core.
/// </summary>
/// <remarks>
/// [INFERENCE] name: 09.3.1-T1's design fixes the <c>RequestInfo</c> parameter of
/// <c>IRegistrationFlow.RegisterAsync</c> and the endpoint signature (<c>RegisterCommand</c>,
/// <c>ISender</c>, <c>CancellationToken</c> only), so the handler cannot receive the value from the
/// endpoint and must resolve it from this port. 09.3.2-T1's login handler needs the same seam.
/// An unregistered accessor resolves to nothing, which the Api's composition root never allows.
/// </remarks>
public interface IRequestInfoAccessor
{
    /// <summary>The caller metadata of the scope; both members read <c>null</c> outside a request.</summary>
    RequestInfo Current { get; }
}
