using Microsoft.Extensions.Options;
using NightTales.Domain.Stories.Structure;

namespace NightTales.Infrastructure.Autopilot;

/// <summary>
/// The bindable shape of <c>Autopilot:Variety</c> (02.8.4-T1 plan "Config keys"): <see cref="VarietyOptions"/> is
/// an immutable Domain record the options system cannot construct, so the section binds here and the registered
/// <c>VarietyOptions</c> is built from it — the same shape as <c>IdeaWeightsOptions</c>.
/// </summary>
/// <remarks>
/// Every key repeats <see cref="VarietyOptions.Default"/> as its in-code default, so a host without the block
/// still behaves exactly as specified. ⚠️ The four numbers are first guesses (plan §10 item 10), calibrated by the
/// 02.8.2-T3 dry run; <c>Enabled=false</c> is the rollback recipe of the task's failure table (steering only).
/// </remarks>
public sealed class VarietyOptionsSection
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Autopilot:Variety";

    /// <summary>Whether the guard rejects and penalises ideas; the batch assessment and steering are produced either way.</summary>
    public bool Enabled { get; set; } = VarietyOptions.Default.Enabled;

    /// <summary>
    /// The number of recent episodes the triple-repeat and tag-share rules look at; never wider than the idea
    /// context's own history cap (<see cref="IdeaContextProvider.HistoryCount"/>), which the validator enforces.
    /// </summary>
    public int WindowK { get; set; } = VarietyOptions.Default.WindowK;

    /// <summary>The number of recent episodes the template-repeat rule looks at; never wider than <see cref="WindowK"/>.</summary>
    public int TemplateWindowK { get; set; } = VarietyOptions.Default.TemplateWindowK;

    /// <summary>The share of the window a tag value may reach before it is forbidden, in <c>(0, 1]</c>.</summary>
    public decimal TagShareMax { get; set; } = VarietyOptions.Default.TagShareMax;

    /// <summary>The count below which the least-used value of a dimension is required (rotation).</summary>
    public int RotationMinCount { get; set; } = VarietyOptions.Default.RotationMinCount;

    /// <summary>The Domain options this section configures.</summary>
    /// <returns>The immutable options the variety guard reads.</returns>
    public VarietyOptions ToOptions() => new(WindowK, TemplateWindowK, TagShareMax, RotationMinCount, Enabled);
}

/// <summary>
/// Validates <c>Autopilot:Variety</c> on start (02.8.4-T1 plan "Config keys"): the two window sizes, the tag-share
/// cap and the rotation floor.
/// </summary>
/// <remarks>
/// A window below one, a window wider than the idea context's history cap (<see cref="IdeaContextProvider.HistoryCount"/>,
/// the number of rows the idea step assesses), a template window wider than the window, a cap outside
/// <c>(0, 1]</c> or a rotation floor below one would silently change every rejection and every steering
/// suggestion of a paid run, so it fails the boot like the <c>Autopilot:Ideas</c> sections do.
/// <c>Enabled=false</c> and every legal value stay valid: they are the rollback recipe.
/// </remarks>
public sealed class VarietyOptionsValidator : IValidateOptions<VarietyOptionsSection>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, VarietyOptionsSection options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.WindowK < 1)
        {
            failures.Add($"'{VarietyOptionsSection.SectionName}:{nameof(VarietyOptionsSection.WindowK)}' must be at least 1.");
        }
        else if (options.WindowK > IdeaContextProvider.HistoryCount)
        {
            // The idea step assesses exactly the rows its context carries, so a wider window would silently
            // assess fewer entries than the story-time plan reads (and an absurd value an unbounded read).
            failures.Add(
                $"'{VarietyOptionsSection.SectionName}:{nameof(VarietyOptionsSection.WindowK)}' must not exceed {IdeaContextProvider.HistoryCount} (the idea context history cap).");
        }

        if (options.TemplateWindowK < 1)
        {
            failures.Add($"'{VarietyOptionsSection.SectionName}:{nameof(VarietyOptionsSection.TemplateWindowK)}' must be at least 1.");
        }
        else if (options.TemplateWindowK > options.WindowK)
        {
            failures.Add(
                $"'{VarietyOptionsSection.SectionName}:{nameof(VarietyOptionsSection.TemplateWindowK)}' must not exceed '{VarietyOptionsSection.SectionName}:{nameof(VarietyOptionsSection.WindowK)}'.");
        }

        if (options.TagShareMax is <= 0m or > 1m)
        {
            failures.Add($"'{VarietyOptionsSection.SectionName}:{nameof(VarietyOptionsSection.TagShareMax)}' must be in (0, 1].");
        }

        if (options.RotationMinCount < 1)
        {
            failures.Add($"'{VarietyOptionsSection.SectionName}:{nameof(VarietyOptionsSection.RotationMinCount)}' must be at least 1.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
