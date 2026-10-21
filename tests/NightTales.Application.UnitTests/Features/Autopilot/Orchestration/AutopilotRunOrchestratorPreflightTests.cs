using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using ErrorOr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Autopilot;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Autopilot.Orchestration;

/// <summary>
/// <see cref="AutopilotRunOrchestrator"/>.AdvanceAsync's pre-flight refusals (02.8.3-T1 acceptance row
/// <c>Orchestrator_MissingUserIdInOutboxPayload_FailsTheStep</c>, task file line 201): a payload without its owner id
/// is refused before any scope, port or database is touched, so the step fails and logs instead of reading as some
/// owner.
/// </summary>
/// <remarks>
/// The orchestrator is built over an empty provider and a <see cref="ThrowingSystemWork"/>: the only way these
/// calls can fail the test is by doing work they must not do — resolving a scoped <c>ISystemContext</c> against an
/// empty provider, or querying the cross-user port.
/// </remarks>
public sealed class AutopilotRunOrchestratorPreflightTests
{
    private static readonly Guid RunId = Guid.Parse("018f0000-0000-7000-8000-00000000001f");

    /// <summary>No owner id: <c>autopilot_user_required</c>, and neither the scope nor the port is used.</summary>
    [Fact]
    public async Task Orchestrator_MissingUserIdInOutboxPayload_FailsTheStep()
    {
        var work = new ThrowingSystemWork();
        var orchestrator = Orchestrator(work);

        var outcome = await orchestrator.AdvanceAsync(RunId, Guid.Empty, CancellationToken.None);

        outcome.IsError.ShouldBeTrue();
        outcome.FirstError.Code.ShouldBe(ErrorCodes.AutopilotUserRequired);
        outcome.FirstError.Type.ShouldBe(ErrorType.Validation);
        work.Calls.ShouldBe(0, "the owner id is checked before any cross-user read");

        var sweep = await orchestrator.AdvanceWhileProgressingAsync(RunId, Guid.Empty, 6, CancellationToken.None);

        sweep.FirstError.Code.ShouldBe(ErrorCodes.AutopilotUserRequired);
        work.Calls.ShouldBe(0);
    }

    private static AutopilotRunOrchestrator Orchestrator(ThrowingSystemWork work) =>
        new(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            work,
            new AutopilotRunOptions(),
            new AutopilotRunMetrics(),
            new FakeClock(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero)),
            NullLogger<AutopilotRunOrchestrator>.Instance);

    /// <summary>A port that must never be reached: any enumeration is a test failure with a readable message.</summary>
    private sealed class ThrowingSystemWork : ISystemWorkQuery
    {
        public int Calls { get; private set; }

        public async IAsyncEnumerable<(Guid Id, Guid UserId)> PendingAsync<T>(
            Expression<Func<T, bool>> where,
            int batch,
            [EnumeratorCancellation] CancellationToken ct)
            where T : Entity, IUserOwned
        {
            Calls++;

            await Task.CompletedTask;

            throw new InvalidOperationException("AdvanceAsync must refuse before any cross-user read.");
#pragma warning disable CS0162 // Unreachable code: kept so the method is a valid async iterator.
            yield break;
#pragma warning restore CS0162
        }

        public async IAsyncEnumerable<(Guid Id, Guid UserId)> PendingAsync<T, TKey>(
            Expression<Func<T, bool>> where,
            Expression<Func<T, TKey>> orderBy,
            int batch,
            [EnumeratorCancellation] CancellationToken ct)
            where T : Entity, IUserOwned
        {
            Calls++;

            await Task.CompletedTask;

            throw new InvalidOperationException("AdvanceAsync must refuse before any cross-user read.");
#pragma warning disable CS0162 // Unreachable code: kept so the method is a valid async iterator.
            yield break;
#pragma warning restore CS0162
        }
    }
}
