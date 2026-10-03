using NightTales.Api.Http;
using NightTales.Application.Common.Errors;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Http;

/// <summary>
/// 11.3.3-T2 / 09 §5.4: <c>render_worker_unavailable</c> is the selector's retry signal. The catalogue half
/// of the contract is checkable before <c>render_jobs</c> exists: the code is registered in exactly one set.
/// The "job row stays <c>queued</c>" half needs the entity and arrives with 09.6.1-T1.
/// </summary>
public sealed class RenderWorkerUnavailableMappingTests
{
    [Fact]
    public void Code_IsTheDocumentedString()
    {
        ErrorCodes.RenderWorkerUnavailable.ShouldBe("render_worker_unavailable");
    }

    [Fact]
    public void Code_IsRegisteredInExactlyOneCatalogueSet()
    {
        var inStatus = ErrorCatalogue.Status.ContainsKey(ErrorCodes.RenderWorkerUnavailable);
        var inNonHttp = ErrorCatalogue.NonHttpCodes.Contains(ErrorCodes.RenderWorkerUnavailable);
        var inAccepted = ErrorCatalogue.AcceptedBodyCodes.Contains(ErrorCodes.RenderWorkerUnavailable);

        ((inStatus ? 1 : 0) + (inNonHttp ? 1 : 0) + (inAccepted ? 1 : 0)).ShouldBe(1);
    }

    [Fact]
    public void Code_IsWorkerInternal_NeverAnHttpStatus()
    {
        // 09 W2 CR-11: the selector's failure is consumed by SubmitRenderJob, which reschedules; it never
        // reaches a ProblemDetails response. The task file's "the API maps it to 503" wording therefore
        // describes 09 §5.4's prose, not an entry in ErrorCatalogue.Status.
        ErrorCatalogue.NonHttpCodes.ShouldContain(ErrorCodes.RenderWorkerUnavailable);
        ErrorCatalogue.Status.ContainsKey(ErrorCodes.RenderWorkerUnavailable).ShouldBeFalse();
    }
}
