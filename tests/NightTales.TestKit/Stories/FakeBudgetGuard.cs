using ErrorOr;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common.Errors;

namespace NightTales.TestKit.Stories;

/// <summary>A scriptable <see cref="IBudgetGuard"/> (02.2.2-T2): allows by default, refuses with <c>spend_cap_reached</c> when told to.</summary>
public sealed class FakeBudgetGuard : IBudgetGuard
{
    private readonly List<decimal> _checked = [];

    /// <summary>When true, every check answers <c>spend_cap_reached</c>.</summary>
    public bool Deny { get; set; }

    /// <summary>The worst cases that were checked, in call order.</summary>
    public IReadOnlyList<decimal> Checked => _checked;

    /// <inheritdoc />
    public ValueTask<ErrorOr<Success>> CheckAsync(CostBound worstCaseUsd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(worstCaseUsd);
        _checked.Add(worstCaseUsd.WorstCaseUsd);

        return ValueTask.FromResult<ErrorOr<Success>>(
            Deny ? Error.Failure(ErrorCodes.SpendCapReached, "The platform's spending limit has been reached.") : Result.Success);
    }
}
