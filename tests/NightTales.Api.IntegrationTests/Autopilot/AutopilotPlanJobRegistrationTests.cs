using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightTales.Application.Abstractions.Flags;
using NightTales.TestKit;
using NightTales.TestKit.Database;
using NightTales.TestKit.Time;
using NightTales.Worker.Jobs;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Autopilot;

/// <summary>
/// The diagnostics of <c>AutopilotPlanJobRegistration</c>'s fail-closed flag read (02.8.3-T1, plan §7.1): the job is
/// unregistered, and the failure is logged by exception type only.
/// </summary>
/// <param name="database">The class's own migrated database, from <see cref="IntegrationTest"/>.</param>
/// <remarks>
/// Metrics and the telemetry pipeline are process-global, and this class starts a Worker host, so it joins the
/// serial collection like every other host-starting class in this folder.
/// </remarks>
[Collection("Process-global host state")]
public sealed class AutopilotPlanJobRegistrationTests(TestDatabase database) : IntegrationTest(database)
{
    /// <summary>Tuesday 20/10/2026 10:00 in Ho Chi Minh City.</summary>
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 3, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A flag store that throws answers "off" — the recurring job is removed, never upserted — and the log carries
    /// the exception's type name only: a flag-store failure can quote the connection string behind it.
    /// </summary>
    [Fact]
    public async Task Registration_FlagReadThrows_LogsTheTypeOnly_AndRemovesTheJob()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var harness = await AutopilotTickHarness.StartAsync(
            Database,
            new FakeClock(Start),
            configureServices: services =>
            {
                services.RemoveAll<IFeatureFlags>();
                services.AddSingleton<IFeatureFlags>(new ThrowingFeatureFlags());
            },
            ct: ct);

        var text = harness.Logs.AllText();
        text.ShouldNotContain(ThrowingFeatureFlags.Secret);

        var failure = harness.Logs.Events
            .Where(logEvent => logEvent.MessageTemplate.Text.StartsWith(
                "jobs.autopilot-plan.flag-read-failed",
                StringComparison.Ordinal))
            .ShouldHaveSingleItem();

        // Type name only: no exception object, so neither its message nor its stack reaches any sink.
        failure.Properties["ExceptionType"].ToString().ShouldBe("\"InvalidOperationException\"");
        failure.Exception.ShouldBeNull();

        harness.Host.Jobs.RecurringRemovals.ShouldContain(AutopilotPlanJobRegistration.RecurringId);
    }

    /// <summary>The flag port refusing the read, with the detail a real store's failure would carry.</summary>
    private sealed class ThrowingFeatureFlags : IFeatureFlags
    {
        /// <summary>The store's own failure text, which must not reach the log.</summary>
        public const string Secret = "Host=flags.internal;Password=hunter2";

        public Task<bool> IsEnabledAsync(string key, CancellationToken ct) =>
            throw new InvalidOperationException(Secret);
    }
}
