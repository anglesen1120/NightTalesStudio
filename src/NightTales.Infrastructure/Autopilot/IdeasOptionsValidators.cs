using System.Globalization;
using Microsoft.Extensions.Options;
using NightTales.Application.Features.Autopilot.Ideas;
using NightTales.Domain.Autopilot.Ideas;

namespace NightTales.Infrastructure.Autopilot;

/// <summary>
/// Validates <c>Autopilot:Ideas:Novelty</c> on start (02.8.1-T2 plan §3.4, F32): the corpus cap and the two
/// thresholds the novelty step rejects on.
/// </summary>
/// <remarks>
/// The thresholds repeat the in-code defaults ([WARN]: they are first guesses, calibrated in the 02.8.2-T3 dry
/// run), so a host that omits them is correct; a value outside its range would silently change the whole pick
/// (a ratio above 1 rejects every candidate), so it fails the boot like the <c>Autopilot:Ideas</c> section does.
/// 1.0 stays legal: the rollback recipe of the task's failure table sets both thresholds to 1.0 to degenerate the
/// pipeline to "pick the first valid candidate".
/// </remarks>
public sealed class IdeaNoveltyOptionsValidator : IValidateOptions<IdeaNoveltyOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, IdeaNoveltyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.CorpusEpisodes < 1 || options.CorpusEpisodes > IdeaCorpusProvider.MaxCorpusEpisodes)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"'{IdeaNoveltyOptions.SectionName}:{nameof(IdeaNoveltyOptions.CorpusEpisodes)}' must be 1..{IdeaCorpusProvider.MaxCorpusEpisodes}."));
        }

        AddRatio(failures, nameof(IdeaNoveltyOptions.LexicalMax), options.LexicalMax);
        AddRatio(failures, nameof(IdeaNoveltyOptions.JudgeMax), options.JudgeMax);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void AddRatio(List<string> failures, string key, decimal value)
    {
        if (value is < 0m or > 1m)
        {
            failures.Add($"'{IdeaNoveltyOptions.SectionName}:{key}' must be 0..1.");
        }
    }
}

/// <summary>
/// Validates <c>Autopilot:Ideas:Policy</c> on start (02.8.1-T2 D12, F31/F33): the match mode must be the one
/// value the Domain's whole-word matcher implements.
/// </summary>
/// <remarks>
/// The three lists are data: an empty <c>CrimeNouns</c>/<c>FramingLexicon</c> only means the framing check finds
/// nothing (a series with no real-world angle), and the banned themes are per-series rows, not keys. Only the
/// match mode can be wrong in a way that silently changes a rejection, so it is the one value pinned.
/// </remarks>
public sealed class IdeaPolicyOptionsValidator : IValidateOptions<IdeaPolicyOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, IdeaPolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return string.Equals(options.BannedThemeMatchMode, IdeaPolicyOptions.WordMatchMode, StringComparison.Ordinal)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"'{IdeaPolicyOptions.SectionName}:{nameof(IdeaPolicyOptions.BannedThemeMatchMode)}' must be '{IdeaPolicyOptions.WordMatchMode}'.");
    }
}

/// <summary>
/// Validates <c>Autopilot:Ideas:Weights</c> on start (02.8.1-T2 plan §3.4): every weight is a 0..1 ratio.
/// </summary>
/// <remarks>
/// A zero weight is legal and disables its component (the rollback recipe sets <c>Variety</c> to 0); the sum need
/// not be one, because <c>IdeaScorer.Score</c> clamps the weighted sum. A negative or above-one weight is the
/// configuration mistake this catches before a paid run picks a candidate with it.
/// </remarks>
public sealed class IdeaWeightsValidator : IValidateOptions<IdeaWeights>
{
    /// <summary>The section every message names.</summary>
    public const string SectionName = "Autopilot:Ideas:Weights";

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, IdeaWeights options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        AddRatio(failures, nameof(IdeaWeights.Novelty), options.Novelty);
        AddRatio(failures, nameof(IdeaWeights.Fit), options.Fit);
        AddRatio(failures, nameof(IdeaWeights.Hook), options.Hook);
        AddRatio(failures, nameof(IdeaWeights.Variety), options.Variety);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void AddRatio(List<string> failures, string key, decimal value)
    {
        if (value is < 0m or > 1m)
        {
            failures.Add($"'{SectionName}:{key}' must be 0..1.");
        }
    }
}
