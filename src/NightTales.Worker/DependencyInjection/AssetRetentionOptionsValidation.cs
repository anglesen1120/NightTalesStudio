using Microsoft.Extensions.Options;
using NightTales.Application.Features.VideoReview;

namespace NightTales.Worker.DependencyInjection;

/// <summary>Fails the start-up on an invalid <c>Assets:RetentionDays</c> (09.7.1-T1, WT56), with every problem
/// listed: the retention window of an approved episode's assets must be a sane number of days.</summary>
/// <remarks>
/// The check lives in Application (<see cref="AssetRetentionOptionsValidator"/>, the
/// <c>RenderIngestOptionsValidation</c> precedent) because that assembly cannot reference
/// <c>Microsoft.Extensions.Options</c>; this is the <see cref="IValidateOptions{T}"/> adapter of the Worker's
/// composition root. The Api binds the same section for its own copy of the approve path and carries its own
/// adapter.
/// </remarks>
internal sealed class AssetRetentionOptionsValidation : IValidateOptions<AssetRetentionOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AssetRetentionOptions options)
    {
        var problems = AssetRetentionOptionsValidator.Validate(options);

        return problems.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(problems.Select(problem => $"{AssetRetentionOptions.SectionName}: {problem}"));
    }
}
