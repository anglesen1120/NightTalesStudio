namespace NightTales.TestKit.Review;

/// <summary>
/// The D15 decision of 02.6.1-T2, as a pure function: which of the two candidate review models the seeded-defect
/// set pins. It owns the two gate thresholds (single source; the live harness and the acceptance asserts reference
/// these constants) and the eligibility rule "recall &gt;= <see cref="RecallThreshold"/> <b>and</b>
/// false-block rate &lt;= <see cref="FalseBlockThreshold"/>, and never on a partial pass".
/// </summary>
/// <remarks>
/// <para>
/// <b>Rule.</b> Luna is pinned when it is eligible; otherwise Sol when it is eligible; otherwise neither, and the
/// prompt is amended and the set re-run (thresholds unchanged). "Cheapest that passes" is the tie-break, not a
/// second comparison: an eligible Luna always wins, even when Sol is also eligible.
/// </para>
/// <para>
/// <b>Partial passes.</b> A pass the USD cap stopped is never eligible, whatever recall it reached, so the two
/// inputs carry both the measured <see cref="SeededDefectReport"/> (which the scorer writes) and the cap flag the
/// harness knows. A report the harness would not score is <see langword="null"/>; a null report is never eligible.
/// </para>
/// </remarks>
public static class SeededModelDecision
{
    /// <summary>The D15 recall gate: a model is eligible when recall is at least this.</summary>
    public const double RecallThreshold = 0.80;

    /// <summary>The D15 false-block gate: a model is eligible when the false-block rate is at most this.</summary>
    public const double FalseBlockThreshold = 0.10;

    /// <summary>Whether one measured pass is eligible: it completed and both D15 gates pass.</summary>
    /// <param name="pass">The pass under test.</param>
    /// <returns><see langword="true"/> when the pass may be pinned.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pass"/> is <see langword="null"/>.</exception>
    public static bool IsEligible(SeededModelPass pass)
    {
        ArgumentNullException.ThrowIfNull(pass);

        // A partial pass reached the cap with drafts left, so its recall is a prefix, not the set's: never eligible.
        return !pass.Partial
            && pass.Report is { } report
            && report.Recall >= RecallThreshold
            && report.FalseBlockRate <= FalseBlockThreshold;
    }

    /// <summary>Applies the D15 rule to both passes.</summary>
    /// <param name="luna">The cheapest candidate's pass.</param>
    /// <param name="sol">The stronger candidate's pass.</param>
    /// <returns>The pinned choice and each model's eligibility.</returns>
    /// <exception cref="ArgumentNullException">A pass is <see langword="null"/>.</exception>
    public static SeededModelDecisionResult Decide(SeededModelPass luna, SeededModelPass sol)
    {
        ArgumentNullException.ThrowIfNull(luna);
        ArgumentNullException.ThrowIfNull(sol);

        var lunaEligible = IsEligible(luna);
        var solEligible = IsEligible(sol);

        var choice = lunaEligible
            ? SeededModelChoice.Luna
            : solEligible
                ? SeededModelChoice.Sol
                : SeededModelChoice.None;

        return new SeededModelDecisionResult(
            choice,
            new SeededModelEligibility(lunaEligible, luna.Report, luna.Partial),
            new SeededModelEligibility(solEligible, sol.Report, sol.Partial));
    }
}

/// <summary>One model's input to the D15 decision: its measured report (null when nothing was scored) and whether the USD cap stopped the pass.</summary>
/// <param name="Report">The scored report, or <see langword="null"/> when the pass scored nothing.</param>
/// <param name="Partial"><see langword="true"/> when the cap stopped the pass before the whole set was reviewed.</param>
public sealed record SeededModelPass(SeededDefectReport? Report, bool Partial = false);

/// <summary>Which candidate model D15 pins; <see cref="None"/> means neither passed and the prompt must be amended.</summary>
public enum SeededModelChoice
{
    /// <summary>Neither model passed the gates.</summary>
    None,

    /// <summary>The cheapest candidate (<c>gpt-6-luna</c>) passed.</summary>
    Luna,

    /// <summary>Only the stronger candidate (<c>gpt-6-sol</c>) passed.</summary>
    Sol,
}

/// <summary>One model's eligibility and the numbers behind it, so the comparison table and the report show why.</summary>
/// <param name="Eligible">Whether the pass may be pinned (both gates pass, not partial).</param>
/// <param name="Report">The scored report, or <see langword="null"/>.</param>
/// <param name="Partial">Whether the cap stopped the pass.</param>
public sealed record SeededModelEligibility(bool Eligible, SeededDefectReport? Report, bool Partial)
{
    /// <summary>The measured recall, or <see langword="null"/> when nothing was scored.</summary>
    public double? Recall => Report?.Recall;

    /// <summary>The measured false-block rate, or <see langword="null"/> when nothing was scored.</summary>
    public double? FalseBlockRate => Report?.FalseBlockRate;
}

/// <summary>The D15 outcome of one seeded-defect run: the pinned choice and each candidate's eligibility.</summary>
/// <param name="Choice">The pinned model.</param>
/// <param name="Luna">The cheapest candidate's eligibility.</param>
/// <param name="Sol">The stronger candidate's eligibility.</param>
public sealed record SeededModelDecisionResult(
    SeededModelChoice Choice,
    SeededModelEligibility Luna,
    SeededModelEligibility Sol);
