using System.Net;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Text;
using NightTales.Domain.Stories.Generation;
using NightTales.Infrastructure.OpenAI;
using Shouldly;
using Xunit;

namespace NightTales.Infrastructure.UnitTests.OpenAI;

/// <summary>02.2.1-T2: the correlation header is set from the ambient <see cref="TextCallScope"/> and from nothing else.</summary>
public sealed class ClientRequestIdHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ClientRequestIdHandler_SetsHeaderFromScope()
    {
        var run = new RunContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, new ReservationId(Guid.NewGuid()), GenerationTrigger.User, 2, "1");
        var capture = new CaptureHandler();
        using var invoker = new HttpMessageInvoker(new ClientRequestIdHandler { InnerHandler = capture });

        using (TextCallScope.Push(run, TextStep.Facts))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/v1/responses");
            using var response = await invoker.SendAsync(request, Ct);
        }

        capture.HeaderValues.ShouldBe([$"{run.GenerationRunId}:facts:2"]);
        TextCallScope.Current.ShouldBeNull("disposing the scope restores the previous value");
    }

    [Fact]
    public async Task ClientRequestIdHandler_BibleStep_UsesRunAndAttemptKey()
    {
        var run = new RunContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, new ReservationId(Guid.NewGuid()), GenerationTrigger.User, 1, "1");
        var capture = new CaptureHandler();
        using var invoker = new HttpMessageInvoker(new ClientRequestIdHandler { InnerHandler = capture });

        using (TextCallScope.Push(run, TextStep.Bible))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/v1/responses");
            using var response = await invoker.SendAsync(request, Ct);
        }

        capture.HeaderValues.ShouldBe([$"{run.GenerationRunId}:bible:1"]);
    }

    [Fact]
    public async Task ClientRequestIdHandler_NoScope_SetsNoHeader()
    {
        var capture = new CaptureHandler();
        using var invoker = new HttpMessageInvoker(new ClientRequestIdHandler { InnerHandler = capture });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/v1/responses");

        using var response = await invoker.SendAsync(request, Ct);

        capture.HeaderValues.ShouldBeEmpty();
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        internal IReadOnlyList<string> HeaderValues { get; private set; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HeaderValues = request.Headers.TryGetValues(ClientRequestIdHandler.HeaderName, out var values) ? [.. values] : [];

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
