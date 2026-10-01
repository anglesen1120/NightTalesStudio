using System.Diagnostics.Metrics;
using NightTales.Application.Common.Errors;
using NightTales.Infrastructure.Security;
using NightTales.TestKit;
using NightTales.TestKit.Api;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Auth;

/// <summary>
/// The two counters of 09.3.2-T1 in the task's Test table and Design ("Metrics/hooks"): every login and
/// every refresh attempt is counted once per outcome code on the <c>NightTales.Auth</c> meter.
/// </summary>
/// <remarks>
/// A <see cref="MeterListener"/> is the whole point of the test: 09.9.3-T1's exporter is just another
/// listener, so what is asserted here is exactly what a scrape will see.
/// </remarks>
public sealed class AuthMetricsTests(TestDatabase database) : IntegrationTest(database)
{
    [Fact]
    public async Task LoginAndRefresh_AreCountedByOutcomeCode()
    {
        var ct = TestContext.Current.CancellationToken;
        var measurements = new List<(string Instrument, string Result)>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, owner) =>
            {
                if (string.Equals(instrument.Meter.Name, AuthMetrics.MeterName, StringComparison.Ordinal))
                {
                    owner.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (string.Equals(tag.Key, AuthMetrics.ResultTagName, StringComparison.Ordinal))
                {
                    measurements.Add((instrument.Name, tag.Value as string ?? string.Empty));
                }
            }
        });

        listener.Start();

        using var factory = new ApiFactory(Database, new FakeClock());
        var user = await AuthTestFlow.CreateUserAsync(factory.Services);
        var client = factory.AnonymousClient();

        var (login, cookie) = await AuthTestFlow.LoginAsync(client, user.Email!, AuthTestFlow.Password, ct);
        login.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

        await AuthTestFlow.LoginAsync(client, user.Email!, "not-the-password-1", ct);

        var (first, rotatedCookie) = await AuthTestFlow.RefreshAsync(client, cookie!, AuthTestFlow.Origin, ct);
        first.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

        await AuthTestFlow.RefreshAsync(client, cookie!, AuthTestFlow.Origin, ct);
        rotatedCookie.ShouldNotBeNull();

        measurements.ShouldContain((AuthMetrics.LoginCounterName, "ok"));
        measurements.ShouldContain((AuthMetrics.LoginCounterName, ErrorCodes.InvalidCredentials));
        measurements.ShouldContain((AuthMetrics.RefreshCounterName, "ok"));
        measurements.ShouldContain((AuthMetrics.RefreshCounterName, ErrorCodes.RefreshTokenReused));
    }
}
